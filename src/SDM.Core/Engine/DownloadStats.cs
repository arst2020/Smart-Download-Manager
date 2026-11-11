using System.Text.Json;
using SDM.Core.Settings;

namespace SDM.Core.Engine;

/// <summary>
/// All-time totals (bytes and files downloaded), kept in stats.json next to the settings.
/// Clearing the list does not reset them.
/// </summary>
public sealed class DownloadStats
{
    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public DateTimeOffset Since { get; set; } = DateTimeOffset.Now;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "stats.json");

    public static DownloadStats Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<DownloadStats>(File.ReadAllText(path)) ?? new DownloadStats();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new DownloadStats();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
