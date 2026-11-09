using System.IO;
using System.Windows;
using SDM.App.Localization;
using SDM.Core.Engine;
using SDM.Core.Models;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>One row of the downloads list; mirrors a <see cref="DownloadJob"/> on the UI thread.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    public DownloadJob Job { get; }

    public DownloadItemViewModel(DownloadJob job)
    {
        Job = job;
        job.Changed += _ => Application.Current?.Dispatcher.InvokeAsync(Refresh);
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(StatusText));
    }

    public string Name => Job.FileName ?? FileNameHelper.FromUrl(Job.Request.Url) ?? Job.Request.Url.Host;

    public string Size => Job.Progress.Total is long t ? FileNameHelper.FormatSize(t) : "";

    public double Percent => Job.Status == DownloadStatus.Completed ? 100 : (Job.Progress.Fraction ?? 0) * 100;

    public string PercentText => Job.Progress.Fraction is not null || Job.Status == DownloadStatus.Completed
        ? $"{Percent:0.0}%"
        : "";

    public string Speed => Job.Status == DownloadStatus.Downloading
        ? $"{FileNameHelper.FormatSize((long)Job.Progress.BytesPerSecond)}/s"
        : "";

    public string Remaining => Job.Status == DownloadStatus.Downloading && Job.Progress.Remaining is TimeSpan r
        ? r.ToString(r.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")
        : "";

    public string StatusText => Job.Status == DownloadStatus.Failed && Job.Error is not null
        ? $"{Loc.Get("Status_Failed")}: {Job.Error}"
        : Loc.Get("Status_" + Job.Status);

    public string Category => Job.Classification?.Explanation ?? Job.Classification?.Category ?? "";

    public string Folder => Job.FilePath is { } p ? Path.GetDirectoryName(p) ?? "" : "";

    public bool IsActive => Job.Status is DownloadStatus.Queued or DownloadStatus.Probing
        or DownloadStatus.Downloading or DownloadStatus.Organizing;

    public bool CanPause => Job.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading;

    public bool CanResume => Job.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled;

    public bool IsCompleted => Job.Status == DownloadStatus.Completed;

    public double BytesPerSecond => Job.Status == DownloadStatus.Downloading ? Job.Progress.BytesPerSecond : 0;

    private void Refresh() => OnPropertyChanged(string.Empty); // refresh all bindings of the row
}
