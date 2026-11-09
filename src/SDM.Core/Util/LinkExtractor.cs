using System.Text.RegularExpressions;

namespace SDM.Core.Util;

/// <summary>Finds download links in pasted text (one per line, or mixed with other text).</summary>
public static partial class LinkExtractor
{
    public sealed record Result(IReadOnlyList<Uri> Links, int Duplicates);

    public static Result Extract(string? text)
    {
        var links = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0;
        if (string.IsNullOrWhiteSpace(text)) return new Result(links, 0);

        foreach (Match m in UrlRegex().Matches(text))
        {
            string raw = m.Value.TrimEnd('.', ',', ';', ')', ']', '"', '\'', '>');
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp) continue;
            if (!seen.Add(uri.AbsoluteUri)) { duplicates++; continue; }
            links.Add(uri);
        }
        return new Result(links, duplicates);
    }

    [GeneratedRegex(@"(?:https?|ftp)://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
