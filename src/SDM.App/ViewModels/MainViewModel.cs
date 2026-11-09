using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using SDM.App.Localization;
using SDM.App.Themes;
using SDM.App.Views;
using SDM.Core;
using SDM.Core.Browser;
using SDM.Core.Media.Ai;
using SDM.Core.Settings;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SmartDownloadService _service;
    private readonly DispatcherTimer _timer;
    private DownloadItemViewModel? _selected;
    private string _filter = "all";
    private string _search = "";
    private string _totalSpeed = "";
    private string _libraryName = "";
    private string _libraryPath = "";
    private bool _libraryConnected;

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queues { get; } = new();
    public bool HasUserQueues => Queues.Count > 1;
    public RelayCommand ManageQueuesCommand { get; }
    public ICollectionView ItemsView { get; }

    public RelayCommand AddCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ItemActionCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ScheduleCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public RelayCommand UpdateNotesCommand { get; }
    public RelayCommand VerifyHashCommand { get; }
    public RelayCommand ExtractCommand { get; }
    public RelayCommand SubtitleCommand { get; }

    public Window? Owner { get; set; }

    /// <summary>The app should quit (an update installer is about to run).</summary>
    public event Action? ExitRequested;

    /// <summary>Raised after the Settings window was saved (the app refreshes the tray and clipboard watcher).</summary>
    public event Action? SettingsSaved;

    public MainViewModel(SmartDownloadService service)
    {
        _service = service;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is DownloadItemViewModel vm && PassesFilter(vm) && vm.Matches(_search);

        service.Queue.JobAdded += job => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new DownloadItemViewModel(job);
            var lastStatus = job.Status;
            job.Changed += j =>
            {
                if (j.Status == lastStatus) return;
                lastStatus = j.Status;
                // Re-filter and recount only on status changes (refreshing on every progress tick would reset selection).
                Application.Current?.Dispatcher.InvokeAsync(RefreshLists, DispatcherPriority.Background);
            };
            Items.Insert(0, vm);
            Selected ??= vm;
            RefreshLists();
        });
        service.Queue.JobRemoved += job => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = Items.FirstOrDefault(i => i.Job == job);
            if (vm is not null) Items.Remove(vm);
            if (Selected == vm) Selected = Items.FirstOrDefault();
            RefreshLists();
        });

        AddCommand = new RelayCommand(AddFromClipboardOrDialog);
        PauseAllCommand = new RelayCommand(() =>
        {
            foreach (var i in Items.Where(i => i.CanPause).ToList()) _service.Queue.Pause(i.Job);
        });
        ItemActionCommand = new RelayCommand(p =>
        {
            if (p is not DownloadItemViewModel item) return;
            if (item.CanPause) _service.Queue.Pause(item.Job);
            else if (item.CanResume) _service.Queue.Resume(item.Job);
            else if (item.IsCompleted) OpenFolder(item);
        });
        PauseCommand = new RelayCommand(p => _service.Queue.Pause(Item(p)!.Job), p => Item(p)?.CanPause == true);
        ResumeCommand = new RelayCommand(p => _service.Queue.Resume(Item(p)!.Job), p => Item(p)?.CanResume == true);
        RemoveCommand = new RelayCommand(p => _service.Queue.Remove(Item(p)!.Job), p => Item(p) is not null);
        OpenFolderCommand = new RelayCommand(p => OpenFolder(Item(p)), p => Item(p) is not null);
        OpenFileCommand = new RelayCommand(p => OpenFile(Item(p)), p => Item(p)?.IsCompleted == true);
        CopyLinkCommand = new RelayCommand(p =>
        {
            string link = Item(p)!.Job.Request.Url.ToString();
            ClipboardWatcher.IgnoreNext(link); // don't offer our own copy back as a new download
            try { Clipboard.SetText(link); } catch { /* clipboard busy */ }
        }, p => Item(p) is not null);
        UndoCommand = new RelayCommand(p => _service.UndoMove(Item(p)!.Job), p => Item(p)?.Job.Move is not null);
        SubtitleCommand = new RelayCommand(p => SubtitleLauncher.Open(_service.Settings, Item(p)!.Job),
            p => SubtitleLauncher.CanSearch(Item(p)?.Job));
        VerifyHashCommand = new RelayCommand(p => ShowHash(Item(p)), p => Item(p)?.FileExists == true);
        ExtractCommand = new RelayCommand(async p =>
        {
            if (Item(p) is not { } item) return;
            if (await _service.ExtractAsync(item.Job) && item.Job.ExtractedTo is { } dest)
                Process.Start("explorer.exe", $"\"{dest}\"");
            else if (item.Job.ExtractError is { } err)
                MessageBox.Show(Owner!, err, Loc.Get("ExtractHere"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }, p => Item(p)?.CanExtract == true);
        InstallUpdateCommand = new RelayCommand(async () => await InstallUpdateAsync(), () => _update is not null && !_updating);
        DismissUpdateCommand = new RelayCommand(() => AvailableUpdate = null);
        UpdateNotesCommand = new RelayCommand(() => OpenUrl(_update?.PageUrl));
        ClearCompletedCommand = new RelayCommand(() => _service.Queue.RemoveCompleted(), () => CountDone > 0);
        SettingsCommand = new RelayCommand(() => ShowSettings());
        ManageQueuesCommand = new RelayCommand(() => ShowSettings(SettingsViewModel.QueuesTab));
        ScheduleCommand = new RelayCommand(ShowScheduler);

        RebuildQueues();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateStatusBar();
        _timer.Start();
        _ = LoadLibraryAsync();
    }

    /// <summary>Commands take the card as parameter; without one they act on the selected card.</summary>
    private DownloadItemViewModel? Item(object? p) => p as DownloadItemViewModel ?? Selected;

    public DownloadItemViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>"all", "active", "done", "queued", "scheduled" or "cat:Series" / "cat:Movie" / …</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            ItemsView.Refresh();
            foreach (var q in Queues) q.RefreshChecked();
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(HeaderSummary));
        }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ItemsView.Refresh(); }
    }

    // Counts for the sidebar
    public int CountAll => Items.Count;
    public int CountActive => Items.Count(i => i.IsActive);
    public int CountDone => Items.Count(i => i.IsDone);
    public int CountQueued => Items.Count(i => i.StatusKey is "Queued" or "Paused" or "Failed");
    public int CountScheduled => Items.Count(i => i.StatusKey == "Scheduled");
    public int CountSeries => Items.Count(i => i.KindKey == "Series");
    public int CountMovie => Items.Count(i => i.KindKey == "Movie");
    public int CountMusic => Items.Count(i => i.KindKey == "Music");
    public int CountSoftware => Items.Count(i => i.KindKey == "Software");
    public int CountDoc => Items.Count(i => i.KindKey == "Doc");

    public string HeaderTitle => _filter switch
    {
        "active" => Loc.Get("FilterActive"),
        "done" => Loc.Get("FilterCompleted"),
        "queued" => Loc.Get("FilterQueued"),
        "scheduled" => Loc.Get("FilterScheduled"),
        "cat:Series" => Loc.Get("CatSeries"),
        "cat:Movie" => Loc.Get("CatMovies"),
        "cat:Music" => Loc.Get("CatMusic"),
        "cat:Software" => Loc.Get("CatSoftware"),
        "cat:Doc" => Loc.Get("CatDocs"),
        { } f when f.StartsWith("queue:") => Queues.FirstOrDefault(q => "queue:" + q.Id == f)?.Name ?? Loc.Get("FilterAll"),
        _ => Loc.Get("FilterAll")
    };

    public string HeaderSummary => string.Format(Loc.Get("HeaderSummary"), CountActive, CountQueued, CountScheduled, CountDone);

    public string TotalSpeed { get => _totalSpeed; private set => Set(ref _totalSpeed, value); }

    public string SpeedLimitText => _service.Settings.GlobalSpeedLimitKBps > 0
        ? $"{Loc.Get("SpeedLimitShort")}: {FileNameHelper.FormatSize(_service.Settings.GlobalSpeedLimitKBps * 1024)}/s"
        : $"{Loc.Get("SpeedLimitShort")}: {Loc.Get("None")}";

    public string ServicesText
    {
        get
        {
            var s = _service.Settings;
            string ai = s.Ai.Provider == AiProvider.None ? Loc.Get("AiNone") : s.Ai.Provider.ToString();
            string tmdb = string.IsNullOrWhiteSpace(s.TmdbApiKey) ? "" : " · TMDB";
            return $"{Loc.Get("AiShort")}: {ai}{tmdb}";
        }
    }

    public string ScheduleText => _service.Settings.Schedule.Enabled
        ? _service.IsScheduleWindowOpen
            ? Loc.Get("ScheduleOpenNow")
            : _service.NextScheduledStart is DateTime next
                ? string.Format(Loc.Get("ScheduleNext"), next.ToString("ddd HH:mm", Loc.Instance.Culture))
                : ""
        : "";

    // Media server card in the sidebar
    public bool HasLibrary => _service.Settings.MediaServer.IsConfigured;
    public string LibraryName { get => _libraryName; private set => Set(ref _libraryName, value); }
    public string LibraryPath { get => _libraryPath; private set => Set(ref _libraryPath, value); }
    public bool LibraryConnected { get => _libraryConnected; private set => Set(ref _libraryConnected, value); }

    private async Task LoadLibraryAsync()
    {
        OnPropertyChanged(nameof(HasLibrary));
        if (!HasLibrary) return;
        try
        {
            var libs = await _service.Advisor.GetLibrariesAsync(_service.Settings, CancellationToken.None, force: true);
            var first = libs.FirstOrDefault();
            LibraryConnected = libs.Count > 0;
            LibraryName = first is null
                ? $"{_service.Settings.MediaServer.Type}"
                : libs.Count == 1 ? $"{first.Server} · {first.Name}" : $"{first.Server} · {libs.Count} {Loc.Get("Libraries")}";
            LibraryPath = first?.Locations.FirstOrDefault() ?? _service.Advisor.LastWarning ?? "";
        }
        catch (Exception ex)
        {
            LibraryConnected = false;
            LibraryName = _service.Settings.MediaServer.Type.ToString();
            LibraryPath = ex.Message;
        }
    }

    private bool PassesFilter(DownloadItemViewModel vm) => _filter switch
    {
        "active" => vm.IsActive,
        "done" => vm.IsDone,
        "queued" => vm.StatusKey is "Queued" or "Paused" or "Failed",
        "scheduled" => vm.StatusKey == "Scheduled",
        { } f when f.StartsWith("cat:") => vm.MatchesCategory(f[4..]),
        { } f when f.StartsWith("queue:") => _service.Queue.EffectiveQueue(vm.Job) == f[6..],
        _ => true
    };

    private void RefreshLists()
    {
        ItemsView.Refresh();
        foreach (var name in new[]
                 {
                     nameof(CountAll), nameof(CountActive), nameof(CountDone), nameof(CountQueued), nameof(CountScheduled),
                     nameof(CountSeries), nameof(CountMovie), nameof(CountMusic), nameof(CountSoftware), nameof(CountDoc),
                     nameof(HeaderSummary)
                 })
            OnPropertyChanged(name);
        foreach (var q in Queues) q.Count = Items.Count(i => _service.Queue.EffectiveQueue(i.Job) == q.Id);
    }

    // ----- Updates -----

    private UpdateInfo? _update;
    private bool _updating;
    private string? _updateStatus;

    /// <summary>Set by the app when GitHub has a newer release.</summary>
    public UpdateInfo? AvailableUpdate
    {
        get => _update;
        set
        {
            _update = value;
            _updateStatus = null;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
            OnPropertyChanged(nameof(UpdateButtonText));
        }
    }

    public bool HasUpdate => _update is not null;

    public string UpdateText => _update is null ? "" : _updateStatus ?? string.Format(Loc.Get("UpdateAvailable"), _update.Version);

    /// <summary>"Install" when the installer can be fetched and checked; "Download" (opens the release page) otherwise.</summary>
    public string UpdateButtonText => CanSelfUpdate ? Loc.Get("UpdateInstall") : Loc.Get("UpdateDownload");

    private bool CanSelfUpdate => _update is { InstallerUrl: not null, ChecksumsUrl: not null } && !AppPaths.IsPortable;

    private void SetUpdateStatus(string text)
    {
        _updateStatus = text;
        OnPropertyChanged(nameof(UpdateText));
    }

    /// <summary>
    /// Installed version: download the installer, check it against the release's SHA256SUMS.txt, ask, then run it
    /// and quit. Portable version (or a release without installer): open the release page.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        if (_update is not { } info) return;
        if (!CanSelfUpdate) { OpenUrl(info.PageUrl); return; }
        _updating = true;
        try
        {
            using var http = HttpClientFactory.CreateApiClient(_service.Settings);
            var progress = new Progress<double>(p => SetUpdateStatus(string.Format(Loc.Get("UpdateDownloading"), (int)(p * 100))));
            string installer = await new UpdateChecker(http).DownloadInstallerAsync(info,
                Path.Combine(Path.GetTempPath(), "SDM-Update"), progress, CancellationToken.None);
            SetUpdateStatus(string.Format(Loc.Get("UpdateReady"), info.Version));
            if (MessageBox.Show(Owner!, string.Format(Loc.Get("UpdateConfirm"), info.Version), Loc.Get("AppTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            // The installer closes nothing by itself here: we quit first; it restarts the app when done.
            Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            SetUpdateStatus(string.Format(Loc.Get("UpdateFailed"), ex.Message));
        }
        finally { _updating = false; }
    }

    private static void OpenUrl(Uri? url)
    {
        if (url is not null && url.Scheme == Uri.UriSchemeHttps)
            try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    // ----- Queues -----

    /// <summary>Main queue + user queues from the settings (rebuilt after the settings change).</summary>
    private void RebuildQueues()
    {
        Queues.Clear();
        foreach (var q in _service.AllQueues)
            Queues.Add(new QueueItemViewModel(this, q, q.Id == SDM.Core.Engine.DownloadQueue.MainQueueId));
        if (Filter.StartsWith("queue:") && Queues.All(q => "queue:" + q.Id != Filter)) Filter = "all";
        OnPropertyChanged(nameof(HasUserQueues));
        RefreshLists();
    }

    public void StartQueue(string id) => _service.Queue.StartQueue(id);
    public void PauseQueue(string id) => _service.Queue.PauseQueue(id);

    /// <summary>"Move to queue" in the card menu (the card is selected when its menu opens).</summary>
    public void MoveSelectedTo(string id)
    {
        if (Selected is null) return;
        _service.Queue.MoveToQueue(Selected.Job, id);
        RefreshLists();
    }

    private readonly SpeedHistory _totalHistory = new(60);

    /// <summary>Mini chart of the total speed in the status bar (100×18 box).</summary>
    public string? TotalSpeedPath { get; private set; }

    private void UpdateStatusBar()
    {
        double total = Items.Sum(i => i.BytesPerSecond);
        TotalSpeed = $"{FileNameHelper.FormatSize((long)total)}/s";
        foreach (var item in Items) item.SampleSpeed();
        if (total > 0 || _totalHistory.Count > 0) _totalHistory.Add(total);
        if (_totalHistory.Count > 0 && _totalHistory.Peak == 0) _totalHistory.Clear(); // idle for a minute: hide
        string path = _totalHistory.ToPath(100, 18);
        TotalSpeedPath = path.Length == 0 ? null : path;
        OnPropertyChanged(nameof(TotalSpeedPath));
        OnPropertyChanged(nameof(ScheduleText));
    }

    // ----- Adding -----

    /// <summary>"Add link": several links on the clipboard open the batch window, otherwise the single Add window.</summary>
    public void AddFromClipboardOrDialog()
    {
        var links = LinkExtractor.Extract(ReadClipboard());
        if (links.Links.Count > 1) ShowBatchDialog(links);
        else ShowAddDialog(links.Links.FirstOrDefault()?.ToString());
    }

    /// <summary>Ctrl+V on the main window.</summary>
    public void PasteLinks()
    {
        var links = LinkExtractor.Extract(ReadClipboard());
        if (links.Links.Count > 1) ShowBatchDialog(links);
        else if (links.Links.Count == 1) ShowAddDialog(links.Links[0].ToString());
    }

    /// <summary>"Download" on the clipboard popup: one link opens the Add window, several the batch window.</summary>
    public void HandleCopiedLinks(IReadOnlyList<Uri> links)
    {
        BringToFront();
        if (links.Count == 1) ShowAddDialog(links[0].ToString());
        else ShowBatchDialog(new LinkExtractor.Result(links, 0));
    }

    /// <summary>A link (or many) arrived from the browser extension or a second app instance.</summary>
    public void HandleBrowserMessage(BrowserMessage message)
    {
        BringToFront();
        var links = message.ValidLinks();
        var headers = message.ToHeaders();
        if (links.Count == 1 && message.Type == "download")
            ShowAddDialog(links[0].ToString(), headers, message.PageTitle, message.FileName);
        else
            ShowBatchDialog(new LinkExtractor.Result(links, 0), headers, message.PageTitle);
    }

    public void BringToFront()
    {
        if (Owner is null) return;
        if (Owner.WindowState == WindowState.Minimized) Owner.WindowState = WindowState.Normal;
        Owner.Show();
        Owner.Activate();
        // Windows only lets the foreground app steal focus; a brief Topmost makes the window visible anyway.
        Owner.Topmost = true;
        Owner.Topmost = false;
        Owner.Focus();
    }

    public void ShowAddDialog(string? url) => ShowAddDialog(url, null, null, null);

    /// <summary>Tray menu "Add link": same as the toolbar button, with the window brought up first.</summary>
    public void AddFromTray()
    {
        BringToFront();
        AddFromClipboardOrDialog();
    }

    public void PauseAll() => PauseAllCommand.Execute(null);

    /// <summary>"2 active · 3.4 MB/s" for the tray tooltip (empty when idle).</summary>
    public string TrayStatus => CountActive == 0 ? "" : $"{string.Format(Loc.Get("TrayActive"), CountActive)} · {TotalSpeed}";

    public void ShowAddDialog(string? url, IReadOnlyDictionary<string, string>? headers, string? pageTitle, string? browserFileName)
    {
        var vm = new AddDownloadViewModel(_service) { Url = url ?? "", Headers = headers, PageTitle = pageTitle };
        if (!string.IsNullOrWhiteSpace(browserFileName)) vm.FileName = browserFileName;
        var window = new AddDownloadWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true || !Uri.TryCreate(vm.Url.Trim(), UriKind.Absolute, out var uri)) return;

        if (vm.BuildRule() is { } rule)
        {
            _service.Settings.Rules.Add(rule);
            SettingsStore.Save(_service.Settings);
        }
        _service.Add(uri, vm.BuildOptions());
        if (vm.Scheduled) EnsureScheduleEnabled();
    }

    private void ShowBatchDialog(LinkExtractor.Result links, IReadOnlyDictionary<string, string>? headers = null, string? pageTitle = null)
    {
        var vm = new BatchAddViewModel(_service, links, headers, pageTitle);
        var window = new BatchAddWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true) return;
        vm.Commit();
        if (vm.Scheduled) EnsureScheduleEnabled();
    }

    /// <summary>Scheduled downloads need a schedule: open the scheduler if it is still off.</summary>
    private void EnsureScheduleEnabled()
    {
        if (!_service.Settings.Schedule.Enabled) ShowScheduler();
    }

    private static string ReadClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { return ""; } // clipboard can be locked by another process
    }

    // ----- Files -----

    private void OpenFolder(DownloadItemViewModel? item)
    {
        string? path = item?.Job.FilePath;
        if (path is not null && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start("explorer.exe", $"\"{_service.Settings.DownloadDirectory}\"");
    }

    private static void OpenFile(DownloadItemViewModel? item)
    {
        if (item?.Job.FilePath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ----- Windows -----

    private void ShowSettings(int tab = 0)
    {
        var vm = new SettingsViewModel(_service.Settings) { SelectedTab = tab };
        var window = new SettingsWindow { DataContext = vm, Owner = Owner };
        vm.OpenScheduler = ShowScheduler;
        if (window.ShowDialog() != true) return;

        AppSettings updated = vm.ToSettings();
        updated.Schedule = _service.Settings.Schedule; // edited in its own window
        SettingsStore.Save(updated);
        _service.UpdateSettings(updated);
        Loc.Instance.SetLanguage(updated.Language);
        ThemeManager.Apply(updated.Theme);
        OnPropertyChanged(string.Empty);
        _ = LoadLibraryAsync();
        RebuildQueues();
        SettingsSaved?.Invoke();
    }

    private void ShowHash(DownloadItemViewModel? item)
    {
        if (item?.Job.FilePath is not { } path) return;
        var vm = new HashViewModel(path);
        var window = new HashWindow { DataContext = vm, Owner = Owner };
        vm.CloseRequested += window.Close;
        window.ShowDialog();
    }

    public void ShowScheduler()
    {
        var vm = new SchedulerViewModel(_service);
        var window = new SchedulerWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true) return;
        _service.Settings.Schedule = vm.ToSchedule();
        SettingsStore.Save(_service.Settings);
        _service.UpdateSettings(_service.Settings);
        OnPropertyChanged(nameof(ScheduleText));
    }
}
