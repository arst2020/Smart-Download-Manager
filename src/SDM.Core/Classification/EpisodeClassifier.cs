using System.Text.RegularExpressions;

namespace SDM.Core.Classification;

/// <summary>
/// Sends TV episodes to "&lt;SeriesRoot&gt;\&lt;Show&gt;\Season NN". If a folder for the show (or season)
/// already exists under a slightly different name, that folder is reused.
/// </summary>
public sealed class EpisodeClassifier : IClassifier
{
    private readonly Func<string> _seriesRoot;

    /// <param name="seriesRoot">Returns the current series root (settings can change at runtime).</param>
    public EpisodeClassifier(Func<string> seriesRoot) => _seriesRoot = seriesRoot;

    public string Name => "episode";

    public ValueTask<ClassificationResult?> ClassifyAsync(ClassificationContext context, CancellationToken ct = default)
    {
        if (!EpisodeParser.TryParse(context.FileName, out var ep, out double confidence))
            return ValueTask.FromResult<ClassificationResult?>(null);

        string root = _seriesRoot();
        string folder = ResolveFolder(root, ep);
        return ValueTask.FromResult<ClassificationResult?>(new ClassificationResult(
            "Series", folder, confidence, Name, ep,
            $"{ep.Show} · S{ep.Season:00}E{ep.Episode:00}"));
    }

    public static string ResolveFolder(string seriesRoot, EpisodeInfo ep)
    {
        string showDir = FindExistingDirectory(seriesRoot, ep.Show) ?? Path.Combine(seriesRoot, ep.Show);
        string seasonDir = FindSeasonDirectory(showDir, ep.Season) ?? Path.Combine(showDir, $"Season {ep.Season:00}");
        return seasonDir;
    }

    internal static string? FindExistingDirectory(string parent, string name)
    {
        if (!Directory.Exists(parent)) return null;
        string target = EpisodeParser.Normalize(name);
        if (target.Length == 0) return null;

        string? best = null;
        double bestScore = 0;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            string candidate = EpisodeParser.Normalize(Path.GetFileName(dir));
            if (candidate.Length == 0) continue;
            double score = candidate == target ? 1.0 : Similarity(candidate, target);
            if (score > bestScore) { bestScore = score; best = dir; }
        }
        return bestScore >= 0.85 ? best : null;
    }

    private static readonly Regex SeasonDirRegex =
        new(@"^(?:season|s|فصل)[\s._\-]*0*(?<n>\d{1,2})$", RegexOptions.IgnoreCase);

    internal static string? FindSeasonDirectory(string showDir, int season)
    {
        if (!Directory.Exists(showDir)) return null;
        foreach (var dir in Directory.EnumerateDirectories(showDir))
        {
            var m = SeasonDirRegex.Match(Util.FileNameHelper.NormalizeDigits(Path.GetFileName(dir)));
            if (m.Success && int.Parse(m.Groups["n"].Value) == season) return dir;
        }
        return null;
    }

    /// <summary>1 - Levenshtein distance / longer length.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}
