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
    };

    private static readonly HashSet<string> EpisodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".flv", ".srt", ".ass", ".sub", ".vtt"
    };

    public static bool TryParse(string fileName, out EpisodeInfo info, out double confidence)
    {
        info = default!;
        confidence = 0;

        string ext = Path.GetExtension(fileName);
        string stem = FileNameHelper.NormalizeDigits(Path.GetFileNameWithoutExtension(fileName));

        foreach (var (regex, conf) in Patterns)
        {
            var m = regex.Match(stem);
            if (!m.Success) continue;

            int season = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 1;
            int episode = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
            string show = CleanShowName(m.Groups["show"].Value);
            if (show.Length == 0 || episode == 0) continue;

            info = new EpisodeInfo(show, season, episode);
            // Files that are not video/subtitles (e.g. "Report S01E02.pdf") are less certain.
            confidence = EpisodeExtensions.Contains(ext) ? conf : conf - 0.25;
            return true;
        }
        return false;
    }

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
