namespace SDM.Core.Classification;

/// <summary>Sorts by file type: extension first, then magic bytes, then the server's MIME type.</summary>
public sealed class TypeClassifier : IClassifier
{
    public static readonly IReadOnlyDictionary<string, string[]> DefaultExtensions = new Dictionary<string, string[]>
    {
        ["Video"] = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".ts", ".mpg", ".mpeg" },
        ["Music"] = new[] { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma" },
        ["Documents"] = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".txt", ".epub", ".csv", ".rtf" },
        ["Compressed"] = new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso" },
        ["Programs"] = new[] { ".exe", ".msi", ".msix", ".appx", ".apk", ".bat", ".cmd" },
        ["Images"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".tif", ".tiff", ".heic" },
        ["Subtitles"] = new[] { ".srt", ".ass", ".vtt", ".sub" },
    };

    private readonly Dictionary<string, string> _byExtension;
    private readonly Func<string, string> _folderFor;

    /// <param name="folderFor">Maps a category to its folder (settings may rename folders).</param>
    public TypeClassifier(Func<string, string>? folderFor = null)
    {
        _folderFor = folderFor ?? (c => c);
        _byExtension = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, exts) in DefaultExtensions)
            foreach (var ext in exts)
                _byExtension[ext] = category;
    }

    public string Name => "type";

    public ValueTask<ClassificationResult?> ClassifyAsync(ClassificationContext context, CancellationToken ct = default)
    {
        string ext = Path.GetExtension(context.FileName);
        if (_byExtension.TryGetValue(ext, out var category))
            return Result(category, 0.9, $"extension {ext}");

        if (context.FilePath is not null && SniffMagic(context.FilePath) is { } magic)
            return Result(magic, 0.85, "file signature");

        if (FromMime(context.ContentType) is { } mime)
            return Result(mime, 0.6, $"MIME {context.ContentType}");

        return ValueTask.FromResult<ClassificationResult?>(null);
    }

    private ValueTask<ClassificationResult?> Result(string category, double confidence, string why) =>
        ValueTask.FromResult<ClassificationResult?>(
            new ClassificationResult(category, _folderFor(category), confidence, Name, null, why));

    internal static string? FromMime(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        string mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mime.StartsWith("video/")) return "Video";
        if (mime.StartsWith("audio/")) return "Music";
        if (mime.StartsWith("image/")) return "Images";
        if (mime is "application/pdf" or "text/plain" or "application/msword" || mime.Contains("officedocument"))
            return "Documents";
        if (mime is "application/zip" or "application/x-rar-compressed" or "application/vnd.rar"
            or "application/x-7z-compressed" or "application/gzip")
            return "Compressed";
        if (mime is "application/x-msdownload" or "application/x-msi" or "application/vnd.microsoft.portable-executable")
            return "Programs";
        return null;
    }

    internal static string? SniffMagic(string path)
    {
        byte[] h = new byte[12];
        int n;
        try
        {
            using var fs = File.OpenRead(path);
            n = fs.Read(h);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        bool Starts(ReadOnlySpan<byte> sig) => n >= sig.Length && h.AsSpan(0, sig.Length).SequenceEqual(sig);

        if (Starts("%PDF"u8)) return "Documents";
        if (Starts("PK\u0003\u0004"u8)) return "Compressed"; // also docx/xlsx, but those have extensions
        if (Starts("Rar!"u8)) return "Compressed";
        if (Starts(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return "Compressed";
        if (Starts(new byte[] { 0x1F, 0x8B })) return "Compressed";
        if (Starts("MZ"u8)) return "Programs";
        if (Starts("ID3"u8) || Starts("fLaC"u8)) return "Music";
        if (Starts(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return "Video"; // Matroska / WebM
        if (n >= 8 && h.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return "Video"; // MP4 family
        if (Starts(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) || Starts(new byte[] { 0xFF, 0xD8, 0xFF })) return "Images";
        return null;
    }
}
