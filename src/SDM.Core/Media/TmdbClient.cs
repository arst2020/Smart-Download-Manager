using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SDM.Core.Media.Ai;

namespace SDM.Core.Media;

public sealed record TmdbMatch(int Id, string Title, int? Year, string? EpisodeTitle = null);

/// <summary>
/// The Movie Database: turns a rough title into the official name and year
/// (e.g. "the office us" → "The Office" 2005). Accepts a v3 API key or a v4 Read Access Token.
/// </summary>
public sealed class TmdbClient(HttpClient http, string credential, string language = "en-US", string baseUrl = "https://api.themoviedb.org/3")
{
    private bool IsBearerToken => credential.StartsWith("eyJ", StringComparison.Ordinal); // JWT

    public Task<TmdbMatch?> FindSeriesAsync(string title, int? year, CancellationToken ct) =>
        SearchAsync("tv", title, year is int y ? $"&first_air_date_year={y}" : "", "name", "first_air_date", ct);

    public Task<TmdbMatch?> FindMovieAsync(string title, int? year, CancellationToken ct) =>
        SearchAsync("movie", title, year is int y ? $"&year={y}" : "", "title", "release_date", ct);

    public async Task<string?> GetEpisodeTitleAsync(int seriesId, int season, int episode, CancellationToken ct)
    {
        try
        {
            var json = await GetAsync($"/tv/{seriesId}/season/{season}/episode/{episode}?language={language}", ct)
                .ConfigureAwait(false);
            return (string?)json["name"];
        }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Used by the settings "Test" button.</summary>
    public async Task<bool> TestAsync(CancellationToken ct)
    {
        await GetAsync("/configuration", ct).ConfigureAwait(false);
        return true;
    }

    private async Task<TmdbMatch?> SearchAsync(string type, string title, string yearFilter, string nameField,
        string dateField, CancellationToken ct)
    {
        string q = Uri.EscapeDataString(title);
        var json = await GetAsync($"/search/{type}?query={q}&language={language}&include_adult=false{yearFilter}", ct)
            .ConfigureAwait(false);
        var results = json["results"]?.AsArray();
        if ((results is null || results.Count == 0) && yearFilter.Length > 0)
        {
            // The year in a file name is sometimes wrong: retry without it.
            json = await GetAsync($"/search/{type}?query={q}&language={language}&include_adult=false", ct).ConfigureAwait(false);
            results = json["results"]?.AsArray();
        }
        var first = results?.FirstOrDefault();
        if (first is null) return null;

        string? date = (string?)first[dateField];
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
        return new TmdbMatch((int)first["id"]!, (string?)first[nameField] ?? title, year);
    }

    private async Task<JsonNode> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        string url = baseUrl.TrimEnd('/') + pathAndQuery;
        if (!IsBearerToken) url += (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(credential);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (IsBearerToken) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await LlmClientFactory.SendJsonAsync(http, request, ct).ConfigureAwait(false);
    }
}
