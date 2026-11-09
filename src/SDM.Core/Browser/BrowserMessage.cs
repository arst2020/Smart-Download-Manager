using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDM.Core.Browser;

/// <summary>
/// What the browser extension sends (through the native host) to the running app.
/// The same shape travels over Chrome native messaging and over the app's named pipe.
/// </summary>
public sealed class BrowserMessage
{
    /// <summary>"download" (one link), "batch" (many links), "ping" (is the app there?), "show" (bring window to front).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "download";

    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("links")] public List<string>? Links { get; set; }

    [JsonPropertyName("referrer")] public string? Referrer { get; set; }

    /// <summary>"name=value; name2=value2" for the link's site, from chrome.cookies.</summary>
    [JsonPropertyName("cookies")] public string? Cookies { get; set; }

    [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }

    /// <summary>File name the browser chose (may be empty).</summary>
    [JsonPropertyName("fileName")] public string? FileName { get; set; }

    [JsonPropertyName("pageTitle")] public string? PageTitle { get; set; }

    /// <summary>Size the browser already knows, or null.</summary>
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>HTTP headers to replay so the server sees the same session as the browser.</summary>
    public Dictionary<string, string> ToHeaders()
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Cookies)) h["Cookie"] = Cookies!;
        if (!string.IsNullOrWhiteSpace(Referrer) && Uri.TryCreate(Referrer, UriKind.Absolute, out _)) h["Referer"] = Referrer!;
        if (!string.IsNullOrWhiteSpace(UserAgent)) h["User-Agent"] = UserAgent!;
        return h;
    }

    /// <summary>Links that are real http(s)/ftp URLs (the single url first, then the batch).</summary>
    public IReadOnlyList<Uri> ValidLinks()
    {
        var all = new List<string>();
        if (Url is not null) all.Add(Url);
        if (Links is not null) all.AddRange(Links);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Uri>();
        foreach (var s in all)
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) continue;
            if (u.Scheme is not ("http" or "https" or "ftp")) continue;
            if (seen.Add(u.AbsoluteUri)) result.Add(u);
        }
        return result;
    }
}

public sealed class BrowserReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }

    public static BrowserReply Success() => new() { Ok = true, Version = typeof(BrowserReply).Assembly.GetName().Version?.ToString(3) };
    public static BrowserReply Failure(string error) => new() { Ok = false, Error = error };
}
