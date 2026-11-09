using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using SDM.App.Localization;
using SDM.App.Views;
using SDM.Core;
using SDM.Core.Engine;
using SDM.Core.Settings;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SmartDownloadService _service;
    private readonly DispatcherTimer _timer;
    private DownloadItemViewModel? _selected;
    private int _filterIndex;
    private string _totalSpeed = "";

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    public RelayCommand AddCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand SettingsCommand { get; }

    public Window? Owner { get; set; }

    public MainViewModel(SmartDownloadService service)
    {
        _service = service;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is DownloadItemViewModel vm && _filterIndex switch
        {
            1 => vm.IsActive,
            2 => vm.IsCompleted,
            _ => true
        };

        service.Queue.JobAdded += job => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new DownloadItemViewModel(job);
            var lastStatus = job.Status;
            // Re-filter only when the status changes (refreshing on every progress tick would reset selection).
            job.Changed += j =>
            {
                if (j.Status == lastStatus) return;
                lastStatus = j.Status;
                if (_filterIndex != 0)
                    Application.Current?.Dispatcher.InvokeAsync(ItemsView.Refresh, DispatcherPriority.Background);
            };
            Items.Insert(0, vm);
        });
        service.Queue.JobRemoved += job => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = Items.FirstOrDefault(i => i.Job == job);
            if (vm is not null) Items.Remove(vm);
        });

        AddCommand = new RelayCommand(() => ShowAddDialog(null));
        PauseCommand = new RelayCommand(() => _service.Queue.Pause(Selected!.Job), () => Selected?.CanPause == true);
        ResumeCommand = new RelayCommand(() => _service.Queue.Resume(Selected!.Job), () => Selected?.CanResume == true);
        RemoveCommand = new RelayCommand(() => _service.Queue.Remove(Selected!.Job), () => Selected is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected is not null);
        UndoCommand = new RelayCommand(UndoMove, () => Selected?.Job.Move is not null);
        SettingsCommand = new RelayCommand(ShowSettings);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
            TotalSpeed = $"{Loc.Get("TotalSpeed")}: {FileNameHelper.FormatSize((long)Items.Sum(i => i.BytesPerSecond))}/s";
        _timer.Start();
    }

    public DownloadItemViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>0 = all, 1 = active, 2 = completed.</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set { if (Set(ref _filterIndex, value)) ItemsView.Refresh(); }
    }

    public string TotalSpeed
    {
        get => _totalSpeed;
        private set => Set(ref _totalSpeed, value);
    }

    public void ShowAddDialog(string? url)
    {
        var vm = new AddDownloadViewModel(_service) { Url = url ?? TryGetClipboardUrl() ?? "" };
        var window = new AddDownloadWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true || !Uri.TryCreate(vm.Url.Trim(), UriKind.Absolute, out var uri)) return;

        if (vm.BuildRule() is { } rule)
        {
            _service.Settings.Rules.Add(rule);
            SettingsStore.Save(_service.Settings);
        }
        _service.Add(uri, vm.BuildOptions());
    }

    private static string? TryGetClipboardUrl()
    {
        try
        {
            string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            return Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
                ? text : null;
        }
        catch { return null; } // clipboard can be locked by another process
    }

    private void OpenFolder()
    {
        string? path = Selected?.Job.FilePath;
        if (path is not null && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start("explorer.exe", $"\"{_service.Settings.DownloadDirectory}\"");
    }

    private void UndoMove()
    {
        if (Selected?.Job is { } job) _service.UndoMove(job);
    }

    private void ShowSettings()
    {
        var vm = new SettingsViewModel(_service.Settings);
        var window = new SettingsWindow { DataContext = vm, Owner = Owner };
        if (window.ShowDialog() != true) return;

        AppSettings updated = vm.ToSettings();
        SettingsStore.Save(updated);
        _service.UpdateSettings(updated);
        Loc.Instance.SetLanguage(updated.Language);
    }
}
