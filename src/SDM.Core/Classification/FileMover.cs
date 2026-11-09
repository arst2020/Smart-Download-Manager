using SDM.Core.Util;

namespace SDM.Core.Classification;

public sealed record MoveRecord(string From, string To, DateTimeOffset When, string Category);

/// <summary>Moves finished files to their classified folder and keeps a history for Undo.</summary>
public sealed class FileMover
{
    private readonly object _lock = new();
    private readonly List<MoveRecord> _history = new();

    public IReadOnlyList<MoveRecord> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    /// <param name="baseDirectory">Relative result folders are resolved against this directory.</param>
    /// <param name="newFileName">Optional new name (e.g. the Emby standard name).</param>
    public MoveRecord? Move(string sourcePath, ClassificationResult result, string baseDirectory, string? newFileName = null)
    {
        string targetDir = Path.GetFullPath(Path.Combine(baseDirectory, result.Folder));
        string currentDir = Path.GetFullPath(Path.GetDirectoryName(sourcePath) ?? baseDirectory);
        string fileName = string.IsNullOrWhiteSpace(newFileName) ? Path.GetFileName(sourcePath) : newFileName;
        if (fileName == Path.GetFileName(sourcePath) && string.Equals(targetDir.TrimEnd(Path.DirectorySeparatorChar), currentDir.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null; // already in place

        Directory.CreateDirectory(targetDir);
        string target = FileNameHelper.MakeUnique(Path.Combine(targetDir, fileName));
        File.Move(sourcePath, target);

        var record = new MoveRecord(sourcePath, target, DateTimeOffset.Now, result.Category);
        lock (_lock) _history.Add(record);
        return record;
    }

    /// <summary>Moves a file back to where it was downloaded.</summary>
    public bool Undo(MoveRecord record)
    {
        if (!File.Exists(record.To)) return false;
        string back = FileNameHelper.MakeUnique(record.From);
        Directory.CreateDirectory(Path.GetDirectoryName(back)!);
        File.Move(record.To, back);
        lock (_lock) _history.Remove(record);
        return true;
    }
}
