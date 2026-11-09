using System.Text.RegularExpressions;
using SDM.Core.Classification;
using SDM.Core.Util;

namespace SDM.Core.Media;

/// <summary>
/// Folder and file names that Emby / Jellyfin / Plex recognise:
///   TV\Dark (2017)\Season 01\Dark - S01E03 - Secrets.mkv
///   Movies\Dune - Part Two (2024)\Dune - Part Two (2024).mkv
/// Existing folders for the same show are reused even if spelled slightly differently.
/// </summary>
public static partial class MediaNamer
{
    public static string SeriesFolder(string root, MediaInfo m)
    {
        string title = Clean(m.Title ?? "Unknown");
        string showDir = FindShowFolder(root, title)
                         ?? Path.Combine(root, m.Year is int y ? $"{title} ({y})" : title);
        int season = m.Season ?? 1;
        return EpisodeClassifier.FindSeasonDirectory(showDir, season) ?? Path.Combine(showDir, $"Season {season:00}");
    }

    public static string MovieFolder(string root, MediaInfo m)
    {
        string name = MovieName(m);
        return FindShowFolder(root, name) ?? Path.Combine(root, name);
    }

    public static string MusicFolder(string root, MediaInfo m)
    {
        string path = root;
        if (!string.IsNullOrWhiteSpace(m.Artist)) path = Path.Combine(path, Clean(m.Artist));
        if (!string.IsNullOrWhiteSpace(m.Album)) path = Path.Combine(path, Clean(m.Album));
        return path;
    }

    /// <summary>Standard file name, or null when the kind has no convention (or data is missing).</summary>
    public static string? StandardFileName(MediaInfo m, string originalFileName)
    {
        string ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(m.Title)) return null;

        switch (m.Kind)
        {
            case MediaKind.Series when m.Episode is int e:
                string name = $"{Clean(m.Title)} - S{m.Season ?? 1:00}E{e:00}";
                if (!string.IsNullOrWhiteSpace(m.EpisodeTitle)) name += " - " + Clean(m.EpisodeTitle);
                return FileNameHelper.Sanitize(name + ext);
            case MediaKind.Movie:
                return FileNameHelper.Sanitize(MovieName(m) + ext);
            default:
                return null;
        }
    }

    private static string MovieName(MediaInfo m) =>
        m.Year is int y ? $"{Clean(m.Title ?? "Unknown")} ({y})" : Clean(m.Title ?? "Unknown");

    /// <summary>Makes a title safe for a path: "Dune: Part Two" → "Dune - Part Two".</summary>
    public static string Clean(string s)
    {
        s = s.Replace(": ", " - ").Replace(':', '-').Replace('/', '-').Replace('\\', '-');
        s = FileNameHelper.Sanitize(s.Trim());
        return MultiSpace().Replace(s, " ");
    }

    /// <summary>Finds "Dark (2017)" or "dark" for the title "Dark".</summary>
    internal static string? FindShowFolder(string root, string title)
    {
        if (!Directory.Exists(root)) return null;
        string target = EpisodeParser.Normalize(StripYear(title));
        if (target.Length == 0) return null;

        string? best = null;
        double bestScore = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            string candidate = EpisodeParser.Normalize(StripYear(Path.GetFileName(dir)));
            if (candidate.Length == 0) continue;
            double score = candidate == target ? 1 : EpisodeClassifier.Similarity(candidate, target);
            if (score > bestScore) { bestScore = score; best = dir; }
        }
        return bestScore >= 0.85 ? best : null;
    }

    private static string StripYear(string s) => YearSuffix().Replace(s, "").Trim();

    [GeneratedRegex(@"\s*[\(\[]?(19|20)\d{2}[\)\]]?\s*$")]
    private static partial Regex YearSuffix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
