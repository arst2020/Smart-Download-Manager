using System.Diagnostics;
using System.IO;
using SDM.App.Localization;
using SDM.Core;
using SDM.Core.Engine;
using SDM.Core.Settings;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>"Download finished" window: Run, Open folder, Close.</summary>
public sealed class CompletionViewModel : ObservableObject
{
    private readonly SmartDownloadService _service;
    private readonly DownloadJob _job;
    private bool _dontShowAgain;

    public CompletionViewModel(SmartDownloadService service, DownloadJob job)
    {
        _service = service;
        _job = job;
        var item = new DownloadItemViewModel(job);
        Badge = item.Badge;
        KindKey = item.KindKey;

        RunCommand = new RelayCommand(() =>
        {
            if (File.Exists(FilePath)) Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            Close();
        }, () => File.Exists(FilePath));
        OpenFolderCommand = new RelayCommand(() =>
        {
            Process.Start("explorer.exe", File.Exists(FilePath) ? $"/select,\"{FilePath}\"" : $"\"{Folder}\"");
            Close();
        });
        CloseCommand = new RelayCommand(Close);
        SubtitleCommand = new RelayCommand(() => SubtitleLauncher.Open(_service.Settings, _job));
    }

    public event Action? CloseRequested;

    public RelayCommand RunCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>Opens the subtitle site's search page in the browser (nothing is downloaded).</summary>
    public RelayCommand SubtitleCommand { get; }

    public bool CanSearchSubtitles => SubtitleLauncher.CanSearch(_job);

    public string FilePath => _job.FilePath ?? "";
    public string FileName => Path.GetFileName(FilePath);
    public string Folder => Path.GetDirectoryName(FilePath) ?? "";
    public string Badge { get; }
    public string KindKey { get; }

    /// <summary>"228 MB · 1:12 · avg 3.2 MB/s".</summary>
    public string Stats
    {
        get
        {
            long size = _job.Progress.Total ?? (File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0);
            var duration = _job.Finished - _job.Started;
            string parts = FileNameHelper.FormatSize(size);
            if (duration is TimeSpan d && d.TotalSeconds >= 1)
            {
                parts += " · " + (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"));
                parts += " · " + string.Format(Loc.Get("Average"), FileNameHelper.FormatSize((long)(size / d.TotalSeconds)) + "/s");
            }
            return parts;
        }
    }

    /// <summary>Shown when the file went into a media server library.</summary>
    public string? LibraryNote => _service.Advisor.CachedLibraryFor(FilePath) is { } lib
        ? string.Format(_service.Settings.MediaServer.RefreshAfterDownload ? Loc.Get("LibraryScanned") : Loc.Get("SavedInLibrary"),
            $"{lib.Server} · {lib.Name}")
        : null;

    public bool DontShowAgain
    {
        get => _dontShowAgain;
        set
        {
            if (!Set(ref _dontShowAgain, value)) return;
            _service.Settings.ShowCompletionDialog = !value;
            SettingsStore.Save(_service.Settings);
        }
    }

    private void Close() => CloseRequested?.Invoke();
}
