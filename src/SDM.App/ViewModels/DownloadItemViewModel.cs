using System.IO;
using System.Windows;
using SDM.App.Localization;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Models;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>One download card; mirrors a <see cref="DownloadJob"/> on the UI thread.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private MediaInfo _media;

    public DownloadJob Job { get; }

    public DownloadItemViewModel(DownloadJob job)
    {
        Job = job;
        _media = MediaNameParser.Parse(job.Request.FileName ?? FileNameHelper.FromUrl(job.Request.Url) ?? "download");
        job.Changed += _ => Application.Current?.Dispatcher.InvokeAsync(Refresh);
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public string FileName => Job.FileName ?? Job.Request.FileName ?? FileNameHelper.FromUrl(Job.Request.Url) ?? Job.Request.Url.Host;

    /// <summary>"Dark (2017) · S01E03" for media, otherwise the file name.</summary>
    public string Title => Job.Classification?.Explanation is { Length: > 0 } e && _media.IsVideo
        ? e
        : _media switch
        {
            { Kind: MediaKind.Series, Title: { } t, Season: int s, Episode: int ep } => $"{t} · S{s:00}E{ep:00}",
            { Kind: MediaKind.Movie, Title: { } t } => _media.Year is int y ? $"{t} ({y})" : t,
            _ => FileName
        };

    /// <summary>Theme key prefix for the icon colours: Series, Movie, Music, Software, Doc, Other.</summary>
    public string KindKey => _media.Kind switch
    {
        MediaKind.Series => "Series",
        MediaKind.Movie => "Movie",
        MediaKind.Music => "Music",
        MediaKind.Software or MediaKind.Archive => "Software",
        MediaKind.Document => "Doc",
        _ => Path.GetExtension(FileName).ToLowerInvariant() is ".mkv" or ".mp4" or ".avi" or ".webm" ? "Movie" : "Other"
    };

    public string Badge => _media.Kind switch
    {
        MediaKind.Series => "TV",
        MediaKind.Movie => "M",
        MediaKind.Music => "♪",
        _ => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant() is { Length: > 0 and <= 4 } ext ? ext : "FILE"
    };

    public string KindLabel => Loc.Get("Kind_" + _media.Kind);

    /// <summary>Active, Paused, Done, Queued, Failed, Scheduled: drives the tag colours.</summary>
    public string StatusKey => Job.Status switch
    {
        DownloadStatus.Probing or DownloadStatus.Downloading or DownloadStatus.Organizing => "Active",
        DownloadStatus.Paused or DownloadStatus.Canceled => "Paused",
        DownloadStatus.Completed => "Done",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Scheduled => "Scheduled",
        _ => "Queued"
    };

    /// <summary>Tag next to the title: the kind while downloading, the state otherwise.</summary>
    public string Tag => StatusKey == "Active" ? KindLabel : Loc.Get("Status_" + Job.Status);

    public string Destination
    {
        get
        {
            string? folder = Job.FilePath is { } p ? Path.GetDirectoryName(p) : Job.ForcedFolder;
            if (folder is null) return Loc.Get("AutoFolderShort");
            var parts = folder.TrimEnd('\\', '/').Split('\\', '/');
            return parts.Length <= 2 ? folder : "…\\" + string.Join("\\", parts[^2..]);
        }
    }

    public string FullFolder => Job.FilePath is { } p ? Path.GetDirectoryName(p) ?? "" : Job.ForcedFolder ?? "";

    public string Size => Job.Progress.Total is long t ? FileNameHelper.FormatSize(t) : "";

    public double Percent => Job.Status == DownloadStatus.Completed ? 100 : (Job.Progress.Fraction ?? 0) * 100;

    public string PercentText => $"{Percent:0}%";

    public bool ShowBar => Job.Status is DownloadStatus.Downloading or DownloadStatus.Probing or DownloadStatus.Paused
        or DownloadStatus.Canceled or DownloadStatus.Organizing || (Job.Status == DownloadStatus.Scheduled && Job.Progress.Downloaded > 0);

    public string SpeedText => Job.Status switch
    {
        DownloadStatus.Downloading => $"{FileNameHelper.FormatSize((long)Job.Progress.BytesPerSecond)}/s",
        DownloadStatus.Completed => Size,
        _ => "—"
    };

    public string MetaText => Job.Status switch
    {
        DownloadStatus.Downloading => string.Join(" · ", new[]
        {
            Job.Progress.Remaining is TimeSpan r ? string.Format(Loc.Get("Remaining"), FormatSpan(r)) : null,
            string.Format(Loc.Get("ConnectionsCount"), Job.Progress.ActiveSegments)
        }.Where(x => x is not null)),
        DownloadStatus.Completed => Job.Finished?.ToLocalTime().ToString("g", Loc.Instance.Culture) ?? "",
        DownloadStatus.Failed => Job.Error ?? "",
        DownloadStatus.Paused or DownloadStatus.Canceled => string.Format(Loc.Get("PausedAt"), PercentText),
        DownloadStatus.Scheduled => Loc.Get("WaitingForSchedule"),
        DownloadStatus.Queued => Loc.Get("WaitingInQueue"),
        _ => Loc.Get("Status_" + Job.Status)
    };

    public bool IsActive => StatusKey == "Active";
    public bool IsPausedLike => StatusKey is "Paused" or "Failed" or "Scheduled" or "Queued";
    public bool IsDone => StatusKey == "Done";
    public bool CanPause => Job.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading;
    public bool CanResume => Job.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Scheduled;
    public bool IsCompleted => Job.Status == DownloadStatus.Completed;

    public double BytesPerSecond => Job.Status == DownloadStatus.Downloading ? Job.Progress.BytesPerSecond : 0;

    /// <summary>Per-connection progress for the connection map (0..1 each).</summary>
    public IReadOnlyList<double> Segments => Job.Progress.SegmentFractions ?? Array.Empty<double>();

    public string SegmentsText => Segments.Count == 0
        ? ""
        : string.Format(Loc.Get("ConnectionsActive"), Job.Progress.ActiveSegments, Segments.Count);

    public string SourceHost => Job.Request.Url.Host;

    public string DetectedBy => Job.Classification?.Source ?? Loc.Get("NotYet");

    public bool MatchesCategory(string kindKey) => KindKey == kindKey;

    public bool Matches(string search) =>
        string.IsNullOrWhiteSpace(search)
        || Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || FileName.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void Refresh()
    {
        if (Job.FileName is { } name) _media = MediaNameParser.Parse(name);
        OnPropertyChanged(string.Empty); // refresh every binding of the card
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
