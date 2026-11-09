using System.Text.Json.Serialization;

namespace SDM.Core.Models;

public enum DownloadStatus
{
    Queued,
    Probing,
    Downloading,
    Organizing,
    Paused,
    Completed,
    Failed,
    Canceled,
    /// <summary>Waiting for the scheduler's time window.</summary>
    Scheduled
}

/// <summary>One byte range of a file, downloaded by its own connection.</summary>
public sealed class Segment
{
    private long _downloaded;

    public int Index { get; set; }

    /// <summary>First byte offset (inclusive).</summary>
    public long Start { get; set; }

    /// <summary>Last byte offset (inclusive). -1 when the total size is unknown.</summary>
    public long End { get; set; }

    public long Downloaded
    {
        get => Interlocked.Read(ref _downloaded);
        set => Interlocked.Exchange(ref _downloaded, value);
    }

    /// <summary>Set when a segment of unknown length reached end of stream.</summary>
    public bool Done { get; set; }

    [JsonIgnore]
    public long Length => End >= Start ? End - Start + 1 : -1;

    [JsonIgnore]
    public bool IsComplete => Done || (End >= Start && Downloaded >= Length);

    internal void Advance(long bytes) => Interlocked.Add(ref _downloaded, bytes);
}

/// <summary>What is persisted to the .sdmstate file next to a partial download.</summary>
public sealed class DownloadState
{
    public int Version { get; set; } = 1;
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public long? TotalSize { get; set; }
    public bool SupportsRange { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public List<Segment> Segments { get; set; } = new();
}

public sealed record ProbeResult(
    Uri FinalUri,
    long? Length,
    bool AcceptRanges,
    string? FileName,
    string? ContentType,
    string? ETag,
    DateTimeOffset? LastModified);

public sealed class DownloadRequest
{
    public required Uri Url { get; init; }
    public required string Directory { get; init; }

    /// <summary>Forces a file name; otherwise taken from the server or URL.</summary>
    public string? FileName { get; init; }

    public int MaxSegments { get; init; } = 8;
    public long MinSegmentSize { get; init; } = 1024 * 1024;
    public int MaxRetries { get; init; } = 5;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra headers, e.g. Cookie / Referer passed by the browser extension.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Optional title of the page the link came from (helps classification).</summary>
    public string? PageTitle { get; init; }
}

public readonly record struct DownloadProgress(long Downloaded, long? Total, double BytesPerSecond, int ActiveSegments)
{
    /// <summary>Progress of each segment (0..1), for the connection map; null when unknown.</summary>
    public IReadOnlyList<double>? SegmentFractions { get; init; }

    public double? Fraction => Total is > 0 ? (double)Downloaded / Total.Value : null;

    public TimeSpan? Remaining =>
        Total is { } t && BytesPerSecond > 1 ? TimeSpan.FromSeconds((t - Downloaded) / BytesPerSecond) : null;
}

public sealed record DownloadResult(string FilePath, long Size, ProbeResult Probe, bool Resumed);
