using System.Text.RegularExpressions;

namespace SDM.Core.Classification;

/// <summary>
/// A user-defined topic, e.g. "Dark" -> D:\Series\Dark, or domain github.com -> D:\Dev.
/// Within one condition type any value may match (OR); every condition type that is set must match (AND).
/// </summary>
public sealed class UserRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Whole-word keywords matched against the file name and page title (case-insensitive).</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Optional regular expression matched against the file name.</summary>
    public string? Pattern { get; set; }

    /// <summary>Source domains; "example.com" also matches "dl.example.com".</summary>
    public List<string> Domains { get; set; } = new();

    /// <summary>Extensions such as ".pdf" or "pdf".</summary>
    public List<string> Extensions { get; set; } = new();

    public string TargetFolder { get; set; } = "";

    /// <summary>When the file is a TV episode, put it in a "Season NN" subfolder of <see cref="TargetFolder"/>.</summary>
    public bool SeasonSubfolders { get; set; } = true;

    public bool HasConditions =>
        Keywords.Count > 0 || !string.IsNullOrWhiteSpace(Pattern) || Domains.Count > 0 || Extensions.Count > 0;
}

public sealed class UserRuleClassifier : IClassifier
{
    private readonly Func<IReadOnlyList<UserRule>> _rules;

    public UserRuleClassifier(Func<IReadOnlyList<UserRule>> rules) => _rules = rules;

    public string Name => "user-rule";

    public ValueTask<ClassificationResult?> ClassifyAsync(ClassificationContext context, CancellationToken ct = default)
    {
        foreach (var rule in _rules())
        {
            if (!rule.Enabled || !rule.HasConditions || string.IsNullOrWhiteSpace(rule.TargetFolder)) continue;
            if (!Matches(rule, context)) continue;

            string folder = rule.TargetFolder;
            EpisodeInfo? episode = null;
            if (EpisodeParser.TryParse(context.FileName, out var ep, out _))
            {
                episode = ep;
                if (rule.SeasonSubfolders)
                    folder = EpisodeClassifier.FindSeasonDirectory(folder, ep.Season)
                             ?? Path.Combine(folder, $"Season {ep.Season:00}");
            }
            return ValueTask.FromResult<ClassificationResult?>(
                new ClassificationResult(rule.Name, folder, 1.0, Name, episode, $"rule: {rule.Name}"));
        }
        return ValueTask.FromResult<ClassificationResult?>(null);
    }

    public static bool Matches(UserRule rule, ClassificationContext ctx)
    {
        string haystack = NormalizeText(ctx.FileName + " " + (ctx.PageTitle ?? ""));

        if (rule.Keywords.Count > 0 && !rule.Keywords.Any(k => ContainsWord(haystack, NormalizeText(k))))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.Pattern))
        {
            try
            {
                if (!Regex.IsMatch(ctx.FileName, rule.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                    return false;
            }
            catch (ArgumentException) { return false; } // invalid user regex
            catch (RegexMatchTimeoutException) { return false; }
        }

        if (rule.Domains.Count > 0)
        {
            string? host = ctx.SourceUrl?.Host;
            if (host is null || !rule.Domains.Any(d => HostMatches(host, d))) return false;
        }

        if (rule.Extensions.Count > 0)
        {
            string ext = Path.GetExtension(ctx.FileName);
            if (!rule.Extensions.Any(e => string.Equals(ext, e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    private static string NormalizeText(string s) =>
        Regex.Replace(Util.FileNameHelper.NormalizeDigits(s).ToLowerInvariant(), @"[._\-]+", " ").Trim();

    private static bool ContainsWord(string haystack, string word) =>
        word.Length > 0 && Regex.IsMatch(haystack, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])");

    private static bool HostMatches(string host, string domain)
    {
        domain = domain.Trim().TrimStart('.').ToLowerInvariant();
        host = host.ToLowerInvariant();
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }
}
