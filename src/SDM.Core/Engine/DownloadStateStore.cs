using System.Text.Json;
using SDM.Core.Models;

namespace SDM.Core.Engine;

/// <summary>Reads and writes the .sdmstate file atomically (write temp, then replace).</summary>
public static class DownloadStateStore
{
    public const string PartExtension = ".sdmpart";
    public const string StateExtension = ".sdmstate";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string statePath, DownloadState state)
    {
        string tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, statePath, overwrite: true);
    }

    public static DownloadState? TryLoad(string statePath)
    {
        try
        {
            if (!File.Exists(statePath)) return null;
            return JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(statePath), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
