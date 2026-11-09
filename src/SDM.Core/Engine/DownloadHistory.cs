using System.Text.Json;
using System.Text.Json.Serialization;
using SDM.Core.Models;

namespace SDM.Core.Engine;

/// <summary>One download as stored in downloads.json.</summary>
public sealed class DownloadRecord
{
    public Guid Id { get; set; }
    public string Url { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? RequestedFileName { get; set; }
    public int Connections { get; set; } = 8;

    /// <summary>Cookie / Referer / User-Agent from the browser, needed to resume session-bound links.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    public string? PageTitle { get; set; }
    public string? ForcedFolder { get; set; }
    public bool IsScheduled { get; set; }
    public string? QueueId { get; set; }
    public DownloadStatus Status { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public long Downloaded { get; set; }
    public long? Total { get; set; }
    public string? Category { get; set; }
    public string? Folder { get; set; }
    public string? Source { get; set; }
    public string? Explanation { get; set; }
    public string? MovedFrom { get; set; }
    public string? MovedTo { get; set; }
    public string? Error { get; set; }
    public string? ExtractedTo { get; set; }
    public DateTimeOffset Added { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
}

/// <summary>Reads and writes the download list (atomic replace, tolerant of a damaged file).</summary>
public static class DownloadHistory
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(SDM.Core.Settings.AppPaths.DataDirectory, "downloads.json");

    public static List<DownloadRecord> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Keep the damaged file for inspection and start with an empty list.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { /* ignore */ }
            return new();
        }
    }

    public static void Save(string path, IEnumerable<DownloadRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records.ToList(), Options));
        File.Move(tmp, path, overwrite: true);
    }
}
