using System.Net;
using System.Text.Json.Nodes;
using SDM.Core.Media.Ai;

namespace SDM.Core.Media;

public enum MediaServerType
{
    None,
    Emby,
    Jellyfin
}

public sealed class MediaServerSettings
{
    public MediaServerType Type { get; set; } = MediaServerType.None;

    /// <summary>e.g. http://localhost:8096 (default port for Emby and Jellyfin).</summary>
    public string Url { get; set; } = "http://localhost:8096";

    /// <summary>Emby/Jellyfin dashboard → API keys.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Ask the server to scan its libraries after a file was saved into one.</summary>
    public bool RefreshAfterDownload { get; set; } = true;

    public bool IsConfigured => Type != MediaServerType.None && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Reads libraries from Emby or Jellyfin and triggers library scans.</summary>
public sealed class MediaServerClient(HttpClient http, MediaServerSettings settings)
{
    private string? _prefix; // Emby 4.x may serve the API under /emby

    public async Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct)
    {
        JsonNode json = await GetAsync("/Library/VirtualFolders", ct).ConfigureAwait(false);
        // Jellyfin and Emby return an array; Emby's /Query variant wraps it in { "Items": [...] }.
        JsonArray items = json as JsonArray ?? json["Items"]?.AsArray() ?? new JsonArray();

        var list = new List<MediaLibrary>();
        foreach (var item in items)
        {
            if (item is null) continue;
            var locations = item["Locations"]?.AsArray().Select(l => (string?)l).OfType<string>().ToList() ?? new();
            if (locations.Count == 0) continue; // e.g. Collections
            string name = (string?)item["Name"] ?? "?";
            list.Add(new MediaLibrary(name, ContentTypeOf(item, name), locations, settings.Type.ToString()));
        }
        return list;
    }

    /// <summary>
    /// Emby 4.8+ keeps the type in LibraryOptions.ContentType and may omit CollectionType;
    /// Jellyfin and older Emby use CollectionType. As a last resort the library name is used.
    /// </summary>
    internal static string ContentTypeOf(JsonNode item, string name)
    {
        string? type = NonEmpty((string?)item["CollectionType"]) ?? NonEmpty((string?)item["LibraryOptions"]?["ContentType"]);
        if (type is not null) return type;

        string n = name.ToLowerInvariant();
        if (n.Contains("tv") || n.Contains("series") || n.Contains("serie") || n.Contains("show") || n.Contains("سریال"))
            return "tvshows";
        if (n.Contains("movie") || n.Contains("film") || n.Contains("فیلم")) return "movies";
        if (n.Contains("music") || n.Contains("musik") || n.Contains("موسیقی")) return "music";
        return "mixed";
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var request = Build(HttpMethod.Post, "/Library/Refresh");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        foreach (string prefix in _prefix is not null ? new[] { _prefix } : new[] { "", "/emby" })
        {
            using var request = Build(HttpMethod.Get, path, prefix);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && prefix.Length == 0) continue;
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{settings.Type}: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            _prefix = prefix;
            return JsonNode.Parse(body) ?? new JsonArray();
        }
        throw new HttpRequestException($"{settings.Type}: API not found at {settings.Url}");
    }

    private HttpRequestMessage Build(HttpMethod method, string path, string? prefix = null)
    {
        var request = new HttpRequestMessage(method, settings.Url.TrimEnd('/') + (prefix ?? _prefix ?? "") + path);
        request.Headers.Add("X-Emby-Token", settings.ApiKey.Trim()); // accepted by Emby and Jellyfin
        return request;
    }
}
