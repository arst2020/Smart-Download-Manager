using System.Globalization;
using System.Text.RegularExpressions;
using SDM.Core.Classification;
using SDM.Core.Util;

namespace SDM.Core.Media;

/// <summary>Fast local detection (no network): episodes, movies "Title.2024.1080p", and file types.</summary>
public static partial class MediaNameParser
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".flv", ".mpg", ".mpeg"
    };

    /// <param name="url">The download link: its folders give the show name when the file name has none.</param>
    public static MediaInfo Parse(string fileName, string? contentType = null, Uri? url = null)
    {
        if (EpisodeParser.TryParse(fileName, url, out var ep, out double epConf))
        {
            return new MediaInfo
            {
                Kind = MediaKind.Series, Title = ep.Show,
                Year = YearBeforeEpisode(fileName) ?? (url is null ? null : EpisodeParser.FromUrlPath(url)?.Year),
                Season = ep.Season, Episode = ep.Episode, Confidence = epConf, Source = "local"
            };
        }

        string ext = Path.GetExtension(fileName);
        if (VideoExtensions.Contains(ext) && TryParseMovie(fileName, out var title, out int movieYear))
        {
            return new MediaInfo
            {
                Kind = MediaKind.Movie, Title = title, Year = movieYear, Confidence = 0.85, Source = "local"
            };
        }

        var kind = KindFromType(fileName, contentType);
        return new MediaInfo
        {
            Kind = kind,
            Title = Path.GetFileNameWithoutExtension(fileName),
            // A bare video without a year could be a movie, a clip or anything: let the AI decide.
            Confidence = kind is MediaKind.Unknown || VideoExtensions.Contains(ext) ? 0.3 : 0.9,
            Source = "local"
        };
    }

    /// <summary>"Dune.Part.Two.2024.1080p.WEB-DL" → ("Dune Part Two", 2024).</summary>
    public static bool TryParseMovie(string fileName, out string title, out int year)
    {
        title = "";
        year = 0;
        string stem = FileNameHelper.NormalizeDigits(Path.GetFileNameWithoutExtension(fileName));
        var m = MovieRegex().Match(stem);
        if (!m.Success) return false;

        year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (year < 1900 || year > DateTime.Now.Year + 1) return false;
        title = EpisodeParser.CleanShowName(m.Groups["title"].Value);
        return title.Length > 0;
    }

    /// <summary>"Dark 2017" → ("Dark", 2017); "Dark" → ("Dark", null).</summary>
    public static (string Title, int? Year) SplitYear(string name)
    {
        var m = TrailingYearRegex().Match(name);
        if (m.Success)
        {
            int y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            string t = name[..m.Index].Trim();
            if (t.Length > 0) return (t, y);
        }
        return (name, null);
    }

    /// <summary>"Shogun.2024.S01E05.mkv" → 2024 (only a year that appears before the episode marker).</summary>
    internal static int? YearBeforeEpisode(string fileName)
    {
        string stem = FileNameHelper.NormalizeDigits(Path.GetFileNameWithoutExtension(fileName));
        var marker = EpisodeMarkerRegex().Match(stem);
        if (!marker.Success) return null;
        var y = YearTokenRegex().Match(stem[..marker.Index]);
        return y.Success ? int.Parse(y.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
    }

    public static MediaKind KindFromType(string fileName, string? contentType)
    {
        string? category = null;
        string ext = Path.GetExtension(fileName);
        foreach (var (cat, exts) in TypeClassifier.DefaultExtensions)
            if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) { category = cat; break; }
        category ??= TypeClassifier.FromMime(contentType);

        return category switch
        {
            "Video" => MediaKind.Unknown, // a video, but movie vs. episode is unknown
            "Music" => MediaKind.Music,
            "Documents" => MediaKind.Document,
            "Compressed" => MediaKind.Archive,
            "Programs" => MediaKind.Software,
            "Images" => MediaKind.Image,
            _ => MediaKind.Unknown
        };
    }

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-(\[]+(?<year>(?:19|20)\d{2})(?!\d)(?:[\s._\-)\]]|$)")]
    private static partial Regex MovieRegex();

    [GeneratedRegex(@"[\s(\[]+(?<y>(?:19|20)\d{2})[\s)\]]*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"(?i)\bS\d{1,2}[\s._\-]*E\d|\b\d{1,2}x\d{2}\b|\bSeason\b|فصل|قسمت")]
    private static partial Regex EpisodeMarkerRegex();

    [GeneratedRegex(@"(?<![\d])(?<y>(?:19|20)\d{2})(?![\d])")]
    private static partial Regex YearTokenRegex();
}
