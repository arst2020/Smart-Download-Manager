using System.Net.Http.Headers;
using System.Text;

namespace SDM.Core.Util;

public static class FileNameHelper
{
    // Windows rules are applied on every OS so names stay portable.
    private static readonly char[] InvalidChars =
        "<>:\"/\\|?*".ToCharArray().Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["video/mp4"] = ".mp4",
        ["video/x-matroska"] = ".mkv",
        ["video/webm"] = ".webm",
        ["audio/mpeg"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["text/plain"] = ".txt",
    };

    /// <summary>Chooses a file name from Content-Disposition, then the URL, then the MIME type.</summary>
    public static string Resolve(ContentDispositionHeaderValue? disposition, Uri url, string? contentType)
    {
        string? name = FromContentDisposition(disposition);
        if (string.IsNullOrWhiteSpace(name))
            name = FromUrl(url);
        if (string.IsNullOrWhiteSpace(name))
            name = "download";

        name = Sanitize(name);

        if (!Path.HasExtension(name) && contentType is not null)
        {
            string mime = contentType.Split(';')[0].Trim();
            if (MimeExtensions.TryGetValue(mime, out var ext))
                name += ext;
        }
        return name;
    }

    public static string? FromContentDisposition(ContentDispositionHeaderValue? cd)
    {
        if (cd is null) return null;
        // FileNameStar (RFC 5987) is already decoded by the parser.
        string? name = cd.FileNameStar ?? cd.FileName;
        return name?.Trim().Trim('"');
    }

    public static string? FromUrl(Uri url)
    {
        string last = url.Segments.Length > 0 ? url.Segments[^1] : "";
        last = Uri.UnescapeDataString(last).Trim('/');
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);

        string result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0) result = "download";

        string stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
            result = "_" + result;

        if (result.Length > 200)
        {
            string ext = Path.GetExtension(result);
            result = result[..(200 - ext.Length)] + ext;
        }
        return result;
    }

    /// <summary>Returns <paramref name="path"/> or "name (2).ext", "name (3).ext"... if it exists.</summary>
    public static string MakeUnique(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path)) return path;

        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>Converts Persian (۰-۹) and Arabic-Indic (٠-٩) digits to ASCII.</summary>
    public static string NormalizeDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is >= '۰' and <= '۹') sb.Append((char)('0' + (c - '۰')));
            else if (c is >= '٠' and <= '٩') sb.Append((char)('0' + (c - '٠')));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }
}
