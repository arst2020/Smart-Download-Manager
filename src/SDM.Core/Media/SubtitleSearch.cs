namespace SDM.Core.Media;

/// <summary>
/// Builds a subtitle-site search link for a downloaded video ("Show S01E03" or "Movie 2024").
/// Only a URL is produced: the app opens it in the browser and never downloads subtitles itself.
/// </summary>
public static class SubtitleSearch
{
    public const string DefaultTemplate = "https://subdl.com/search/{query}";

    /// <summary>Ready-made choices for the settings page (the user can type any other site).</summary>
    public static readonly IReadOnlyList<(string Name, string Template)> Presets = new[]
    {
        ("SubDL", DefaultTemplate),
        ("OpenSubtitles", "https://www.opensubtitles.com/en/search-all/q-{query}"),
        ("Google", "https://www.google.com/search?q={query}+subtitles"),
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".flv", ".mpg", ".mpeg"
    };

    public static bool IsVideo(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && VideoExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>"Breaking.Bad.S01E03.720p.mkv" → "Breaking Bad S01E03"; "Dune.Part.Two.2024.1080p.mkv" → "Dune Part Two 2024".</summary>
    public static string BuildQuery(string fileName)
    {
        var info = MediaNameParser.Parse(fileName);
        string title = Clean(info.Title ?? Path.GetFileNameWithoutExtension(fileName));
        return info.Kind switch
        {
            MediaKind.Series when info.Season is int s && info.Episode is int e => $"{title} S{s:00}E{e:00}",
            MediaKind.Movie when info.Year is int y => $"{title} {y}",
            _ => title
        };
    }

    /// <summary>
    /// Fills the template. Returns null unless the result is an http(s) address, so a bad setting can never
    /// make the app start a program or open a local file.
    /// </summary>
    public static Uri? BuildUrl(string? template, string query)
    {
        if (string.IsNullOrWhiteSpace(template)) template = DefaultTemplate;
        template = template.Trim();
        string encoded = Uri.EscapeDataString(query.Trim());
        string url = template.Contains("{query}", StringComparison.OrdinalIgnoreCase)
            ? template.Replace("{query}", encoded, StringComparison.OrdinalIgnoreCase)
            : template + encoded;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }

    private static string Clean(string s) =>
        string.Join(' ', s.Replace('.', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
