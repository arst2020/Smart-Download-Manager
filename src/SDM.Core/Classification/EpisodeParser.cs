using System.Globalization;
using System.Text.RegularExpressions;
using SDM.Core.Util;

namespace SDM.Core.Classification;

/// <summary>
/// Extracts show name, season and episode from a file name.
/// Supports S01E03, s1e3, 1x03, "Season 1 Episode 3", "فصل ۱ قسمت ۳" and "قسمت ۳".
/// </summary>
public static partial class EpisodeParser
{
    private const string Sep = @"[\s._\-\[\]()]*";

    private static readonly (Regex Regex, double Confidence)[] Patterns =
    {
        (new Regex(@"^(?<show>.*?)" + Sep + @"\bS(?<s>\d{1,2})" + Sep + @"E(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase), 0.95),
        (new Regex(@"^(?<show>.*?)" + Sep + @"\bSeason" + Sep + @"(?<s>\d{1,2})" + Sep + @"(?:Episode|Ep)" + Sep + @"(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase), 0.95),
        (new Regex(@"^(?<show>.*?)" + Sep + @"فصل" + Sep + @"(?<s>\d{1,2})" + Sep + @"(?:قسمت|ق)" + Sep + @"(?<e>\d{1,3})(?!\d)"), 0.95),
        (new Regex(@"^(?<show>.*?)" + Sep + @"\b(?<s>\d{1,2})x(?<e>\d{2,3})\b", RegexOptions.IgnoreCase), 0.9),
        (new Regex(@"^(?<show>.*?)" + Sep + @"قسمت" + Sep + @"(?<e>\d{1,3})(?!\d)"), 0.85),
        (new Regex(@"^(?<show>.*?)" + Sep + @"\b(?:Episode|Ep)" + Sep + @"(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase), 0.8),
        // "Shahrzad.E05.720p" (episode without season, common on Persian sites)
        (new Regex(@"^(?<show>.*?)" + Sep + @"\bE(?<e>\d{1,3})(?![\dp])", RegexOptions.IgnoreCase), 0.75),
    };

    private static readonly HashSet<string> EpisodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".flv", ".srt", ".ass", ".sub", ".vtt"
    };

    public static bool TryParse(string fileName, out EpisodeInfo info, out double confidence) =>
        TryParse(fileName, null, out info, out confidence);

    /// <summary>
    /// Like <see cref="TryParse(string, out EpisodeInfo, out double)"/>, but when the file name has no show name
    /// ("S01E03.mkv", "E05.mkv", "01.mp4") the show and season are taken from the link's folders
    /// (".../Breaking.Bad/S01/S01E03.mkv"), so different shows don't end up in one folder.
    /// </summary>
    public static bool TryParse(string fileName, Uri? url, out EpisodeInfo info, out double confidence)
    {
        info = default!;
        confidence = 0;

        string ext = Path.GetExtension(fileName);
        string stem = FileNameHelper.NormalizeDigits(Path.GetFileNameWithoutExtension(fileName));
        PathHint? hint = null;
        bool hintRead = false;
        PathHint? Hint()
        {
            if (!hintRead) { hint = url is null ? null : FromUrlPath(url); hintRead = true; }
            return hint;
        }

        foreach (var (regex, conf) in Patterns)
        {
            var m = regex.Match(stem);
            if (!m.Success) continue;

            int episode = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
            string show = CleanShowName(m.Groups["show"].Value);
            double c = conf;
            int? season = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : null;
            if (show.Length == 0)
            {
                if (Hint() is not { Show: { } fromPath } h) continue;
                show = fromPath;
                season ??= h.Season;
                c -= 0.1; // show name from the folder, not the file
            }
            if (episode == 0) continue;

            info = new EpisodeInfo(show, season ?? 1, episode);
            confidence = EpisodeExtensions.Contains(ext) ? c : c - 0.25;
            return true;
        }

        // "01.mp4" inside ".../Dark/S01/" : a bare number is an episode only when the folder says which season.
        if (EpisodeExtensions.Contains(ext) && BareNumberRegex().Match(stem) is { Success: true } bare
            && Hint() is { Show: { } show2, Season: int season2 })
        {
            int episode = int.Parse(bare.Groups["e"].Value, CultureInfo.InvariantCulture);
            if (episode > 0)
            {
                info = new EpisodeInfo(show2, season2, episode);
                confidence = 0.7;
                return true;
            }
        }
        return false;
    }

    /// <summary>Show name, season and year found in a link's folders.</summary>
    public sealed record PathHint(string? Show, int? Season, int? Year);

    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "series", "serial", "serials", "tv", "tvshow", "tvshows", "tv-shows", "tv shows", "shows", "show", "episodes",
        "movies", "movie", "film", "films", "video", "videos", "media", "download", "downloads", "dl", "files", "file",
        "upload", "uploads", "content", "data", "pub", "public", "ftp", "share", "shared", "mnt", "home", "www", "new",
        "dubbed", "softsub", "hardsub", "subbed", "sub", "dub", "hd", "sd", "fhd", "uhd", "4k",
        "سریال", "سریال‌ها", "فیلم", "دانلود", "دوبله", "زیرنویس", "زیرنویس-چسبیده"
    };

    /// <summary>
    /// Walks the link's folders from the file upwards: season folders ("S01", "Season 2", "فصل 1") give the season,
    /// quality/generic folders ("720p", "Series", "Download") are skipped, the first other folder is the show.
    /// </summary>
    public static PathHint? FromUrlPath(Uri url)
    {
        if (!url.IsAbsoluteUri) return null;
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => FileNameHelper.NormalizeDigits(Uri.UnescapeDataString(seg)).Trim())
            .ToList();
        if (segments.Count < 2) return null;
        segments.RemoveAt(segments.Count - 1); // the file itself

        int? season = null;
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            string seg = segments[i];
            var sm = SeasonFolderRegex().Match(seg);
            if (sm.Success)
            {
                season ??= int.Parse(sm.Groups["s"].Value, CultureInfo.InvariantCulture);
                // "Breaking.Bad.S01" holds the show too
                string before = CleanShowName(seg[..sm.Index].Replace('-', ' '));
                if (before.Length > 1 && !GenericFolders.Contains(before)) return Make(before, season);
                continue;
            }
            if (seg.Length < 2 || GenericFolders.Contains(seg) || NoiseFolderRegex().IsMatch(seg) || seg.All(char.IsDigit)) continue;
            return Make(seg.Replace('-', ' '), season);
        }
        return season is null ? null : new PathHint(null, season, null);

        static PathHint Make(string raw, int? season)
        {
            string cleanedYear = raw.Replace('.', ' ').Replace('_', ' ');
            var y = YearRegex().Match(cleanedYear);
            int? year = y.Success ? int.Parse(y.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
            string show = CleanShowName(raw);
            return new PathHint(show.Length > 0 ? show : null, season, year);
        }
    }

    [GeneratedRegex(@"^\s*(?:E|Ep|Episode|قسمت)?[\s._\-]*(?<e>\d{1,3})(?:[\s._\-].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareNumberRegex();

    [GeneratedRegex(@"(?:^|[\s._\-])(?:S|Season|فصل)[\s._\-]*(?<s>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"^(?:\d{3,4}p|x26[45]|h\.?26[45]|hevc|web-?dl|webrip|bluray|brrip|hdtv|10bit|8bit)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseFolderRegex();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?!\d)")]
    private static partial Regex YearRegex();

    public static string CleanShowName(string raw)
    {
        string s = raw;
        s = LeadingTagRegex().Replace(s, "");          // "[Group] Show" -> "Show"
        s = s.Replace('.', ' ').Replace('_', ' ');
        s = TrailingYearRegex().Replace(s, "");         // "Show 2017" / "Show (2017)" -> "Show"
        s = MultiSpaceRegex().Replace(s, " ");
        s = s.Trim(' ', '-', '(', '[', ']', ')');

        if (s.Length > 0 && s == s.ToLowerInvariant() && s.Any(char.IsAsciiLetter))
            s = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
        return s;
    }

    /// <summary>Lower-case letters and digits only; used to match existing folders loosely.</summary>
    public static string Normalize(string s) =>
        new string(FileNameHelper.NormalizeDigits(s).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex(@"^\s*(\[[^\]]*\]\s*)+")]
    private static partial Regex LeadingTagRegex();

    [GeneratedRegex(@"[\s(\[]*(19|20)\d{2}[\s)\]]*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
