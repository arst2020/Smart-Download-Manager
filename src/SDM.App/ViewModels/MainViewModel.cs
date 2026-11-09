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

    public Window? Owner { get; set; }

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
            try { Clipboard.SetText(Item(p)!.Job.Request.Url.ToString()); } catch { /* clipboard busy */ }
        }, p => Item(p) is not null);
        UndoCommand = new RelayCommand(p => _service.UndoMove(Item(p)!.Job), p => Item(p)?.Job.Move is not null);
        SettingsCommand = new RelayCommand(ShowSettings);
        ScheduleCommand = new RelayCommand(ShowScheduler);

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
    }

    private void UpdateStatusBar()
    {
        TotalSpeed = $"{FileNameHelper.FormatSize((long)Items.Sum(i => i.BytesPerSecond))}/s";
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

    public void ShowAddDialog(string? url)
    {
        var vm = new AddDownloadViewModel(_service) { Url = url ?? "" };
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

    private void ShowBatchDialog(LinkExtractor.Result links)
    {
        var vm = new BatchAddViewModel(_service, links);
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

    private void ShowSettings()
    {
        var vm = new SettingsViewModel(_service.Settings);
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
