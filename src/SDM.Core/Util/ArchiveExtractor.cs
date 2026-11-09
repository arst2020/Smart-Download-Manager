using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SDM.Core.Util;

/// <summary>
/// Extracts finished archives into a folder next to them: .zip with .NET itself; .rar, .7z, .tar, .gz … with
/// 7-Zip when it is installed. Multi-part archives are extracted from their first part only.
/// </summary>
public static partial class ArchiveExtractor
{
    private static readonly string[] SevenZipTypes = { ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst" };

    /// <summary>Path of 7z.exe, or null. Can be set for tests or a portable 7-Zip.</summary>
    public static string? SevenZipPath { get; set; } = FindSevenZip();

    public static bool IsArchive(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".zip" || SevenZipTypes.Contains(ext) || NumberedRegex().IsMatch(Path.GetFileName(path).ToLowerInvariant());
    }

    /// <summary>True for archives this PC can extract and that are not the 2nd, 3rd … part of a set.</summary>
    public static bool CanExtract(string path)
    {
        if (!IsArchive(path) || IsLaterPart(path)) return false;
        return Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) || SevenZipPath is not null;
    }

    /// <summary>"movie.part2.rar", "movie.r00", "data.7z.002", "archive.z01" → true ("…part1.rar" / ".001" → false).</summary>
    public static bool IsLaterPart(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        var part = PartRegex().Match(name);
        if (part.Success) return int.Parse(part.Groups[1].Value) > 1;
        var numbered = NumberedRegex().Match(name);
        if (numbered.Success) return int.Parse(numbered.Groups[1].Value) > 1;
        return OldRarRegex().IsMatch(name) || ZipSplitRegex().IsMatch(name);
    }

    /// <summary>"D:\Downloads\Tool.zip" → "D:\Downloads\Tool" (or "Tool (2)" if that exists).</summary>
    public static string DestinationFor(string archive)
    {
        string dir = Path.GetDirectoryName(archive) ?? ".";
        string name = Path.GetFileNameWithoutExtension(archive);
        name = PartSuffixRegex().Replace(name, "");
        // "data.7z.001" -> "data"
        if (NumberedRegex().IsMatch(Path.GetFileName(archive).ToLowerInvariant())) name = Path.GetFileNameWithoutExtension(name);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        string dest = Path.Combine(dir, name);
        for (int i = 2; Directory.Exists(dest) || File.Exists(dest); i++) dest = Path.Combine(dir, $"{name} ({i})");
        return dest;
    }

    /// <summary>Extracts and returns the destination folder. Throws if the archive is damaged, protected or unsupported.</summary>
    public static async Task<string> ExtractAsync(string archive, bool deleteArchive = false, CancellationToken ct = default)
    {
        if (!CanExtract(archive)) throw new NotSupportedException($"Cannot extract {Path.GetFileName(archive)}");
        string dest = DestinationFor(archive);
        try
        {
            if (Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                // .NET refuses entries that would land outside the destination ("zip slip").
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, dest), ct).ConfigureAwait(false);
            else
                await RunSevenZipAsync(archive, dest, ct).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(dest) && !Directory.EnumerateFileSystemEntries(dest).Any()) Directory.Delete(dest);
            throw;
        }
        if (deleteArchive) File.Delete(archive);
        return dest;
    }

    private static async Task RunSevenZipAsync(string archive, string dest, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(SevenZipPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // closed at once: a password prompt fails instead of hanging
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "x", "-y", "-bd", $"-o{dest}", archive }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException("7-Zip did not start");
        process.StandardInput.Close();
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        if (process.ExitCode != 0)
        {
            string err = (await errorTask.ConfigureAwait(false)).Trim();
            throw new IOException($"7-Zip: {(err.Length > 0 ? err.Split('\n').Last().Trim() : "exit code " + process.ExitCode)}");
        }
    }

    private static string? FindSevenZip()
    {
        var candidates = new List<string>();
        foreach (var env in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } pf) candidates.Add(Path.Combine(pf, "7-Zip", "7z.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "7z.exe" : "7z"));
        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"\.part0*(\d+)\.rar$")] private static partial Regex PartRegex();
    [GeneratedRegex(@"\.(?:7z|zip|rar)\.0*(\d+)$")] private static partial Regex NumberedRegex();
    [GeneratedRegex(@"\.r\d\d$")] private static partial Regex OldRarRegex();
    [GeneratedRegex(@"\.z\d\d$")] private static partial Regex ZipSplitRegex();
    [GeneratedRegex(@"\.part0*\d+$", RegexOptions.IgnoreCase)] private static partial Regex PartSuffixRegex();
}
