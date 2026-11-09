using System.Text.RegularExpressions;
using SDM.Core.Classification;

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

    private static readonly HashSet<string> DownloadableExtensions = BuildDownloadable();

    private static HashSet<string> BuildDownloadable()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, exts) in TypeClassifier.DefaultExtensions)
        {
            // Copying an image or a page link is everyday work, not a download wish.
            if (category is "Images") continue;
            foreach (var e in exts) set.Add(e);
        }
        set.Remove(".txt");
        set.Remove(".csv");
        foreach (var e in new[] { ".dmg", ".pkg", ".deb", ".rpm", ".img", ".bin", ".zst", ".tgz", ".cab", ".jar" }) set.Add(e);
        return set;
    }

    /// <summary>
    /// True when the link's path ends in a file type people download (video, archive, installer, document …).
    /// Used by the clipboard watcher so copying ordinary page links stays quiet.
    /// </summary>
    public static bool LooksDownloadable(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeFtp) return !uri.AbsolutePath.EndsWith('/');
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        string ext = Path.GetExtension(path);
        return ext.Length > 1 && DownloadableExtensions.Contains(ext);
    }

    /// <summary>The links in <paramref name="text"/> that look like downloads (see <see cref="LooksDownloadable"/>).</summary>
    public static IReadOnlyList<Uri> ExtractDownloadable(string? text) =>
        Extract(text).Links.Where(LooksDownloadable).ToList();

    [GeneratedRegex(@"(?:https?|ftp)://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
