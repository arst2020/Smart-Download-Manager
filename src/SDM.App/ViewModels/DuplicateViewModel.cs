using System.IO;
using SDM.App.Localization;
using SDM.Core.Engine;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>What the user picked in the duplicate window.</summary>
public enum DuplicateChoice { Cancel, OpenExisting, Resume, NewName, Replace }

/// <summary>
/// "This file already exists": shows what we already have (same link, same name before or after renaming,
/// or the same episode / movie in another quality) and lets the user decide, like IDM.
/// </summary>
public sealed class DuplicateViewModel : ObservableObject
{
    private readonly DuplicateMatch _match;

    /// <param name="batchCount">Batch mode: how many of the added links are duplicates (one question for all).</param>
    public DuplicateViewModel(IReadOnlyList<DuplicateMatch> matches, string newName, long? newSize, int batchCount = 0)
    {
        _match = matches[0];
        NewName = newName;
        NewSize = newSize is long n ? FileNameHelper.FormatSize(n) : "?";
        BatchCount = batchCount;
        OtherCount = Math.Max(0, matches.Count - 1);
        Choose = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse(s, out DuplicateChoice c)) { Choice = c; Closed?.Invoke(); }
        });
    }

    public event Action? Closed;
    public RelayCommand Choose { get; }
    public DuplicateChoice Choice { get; private set; } = DuplicateChoice.Cancel;

    /// <summary>"Always do this" (saved as the duplicate setting).</summary>
    public bool Remember { get; set; }

    public bool IsBatch => BatchCount > 0;
    public int BatchCount { get; }
    public int OtherCount { get; }

    public string Heading => IsBatch ? string.Format(Loc.Get("DupBatchHeading"), BatchCount) : Loc.Get("DupHeading");

    public string Explanation => (_match.Kind, _match.Reason) switch
    {
        (DuplicateKind.InProgress, _) => Loc.Get("DupInProgress"),
        (_, DuplicateReason.SameLink) => Loc.Get("DupSameLink"),
        (_, DuplicateReason.SameEpisode) => Loc.Get("DupSameEpisode"),
        (_, DuplicateReason.SameMovie) => Loc.Get("DupSameMovie"),
        _ => Loc.Get("DupSameName"),
    };

    public string NewName { get; }
    public string NewSize { get; }
    public string ExistingName => Path.GetFileName(_match.ExistingPath ?? _match.Job?.FileName ?? _match.Job?.Request.Url.AbsolutePath ?? "");
    public string ExistingFolder => Path.GetDirectoryName(_match.ExistingPath ?? "") ?? "";
    public string ExistingSize => _match.ExistingSize is long s ? FileNameHelper.FormatSize(s) : "?";
    public string ExistingDate => _match.ExistingDate?.ToString("g", Loc.Instance.Culture) ?? "";
    public string? OthersText => OtherCount > 0 ? string.Format(Loc.Get("DupOthers"), OtherCount) : null;

    public bool CanOpen => !IsBatch && _match.ExistingPath is { } p && File.Exists(p);
    public bool CanResume => !IsBatch && _match.Kind == DuplicateKind.InProgress && _match.Job is not null;
    public bool CanReplace => _match.Kind != DuplicateKind.InProgress || _match.Job is not null;
}
