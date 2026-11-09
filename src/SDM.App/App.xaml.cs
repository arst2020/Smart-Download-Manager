using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SDM.App.Localization;
using SDM.App.Themes;
using SDM.App.ViewModels;
using SDM.App.Views;
using SDM.Core;
using SDM.Core.Browser;
using SDM.Core.Engine;
using SDM.Core.Scheduling;
using SDM.Core.Settings;
using SDM.Core.Util;

namespace SDM.App;

public partial class App : Application
{
    private SmartDownloadService? _service;
    private AppPipeServer? _pipe;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private ClipboardWatcher? _clipboard;
    private DispatcherTimer? _trayTimer;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The window's close button may only hide it (tray); the app ends through ExitApp().
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppSettings settings = SettingsStore.Load();
        Loc.Instance.SetLanguage(settings.Language);
        ThemeManager.Apply(settings.Theme);

        _service = new SmartDownloadService(settings);
        _service.Queue.JobCompleted += job => Dispatcher.InvokeAsync(() => OnJobCompleted(job));
        _service.Queue.JobFailed += job => Dispatcher.InvokeAsync(() => OnJobFailed(job));
        _service.ScheduledQueueFinished += action => Dispatcher.InvokeAsync(() => AfterScheduledQueue(action));

        var vm = new MainViewModel(_service);
        _vm = vm;
        var window = new MainWindow { DataContext = vm };
        vm.Owner = window;
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        vm.SettingsSaved += ApplySettings;
        vm.ExitRequested += ExitApp;
        // "--tray" (Windows startup): start hidden in the notification area.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) window.Show();

        // Restore the list from last time (after the view model exists, so it sees every job).
        if (settings.KeepHistory) _service.EnableHistory();

        StartTray();
        StartClipboardWatcher(window, settings);
        StartBrowserIntegration(settings);
        if (settings.CheckForUpdates) _ = CheckForUpdatesAsync();

        // "SmartDownloadManager.exe <url>" opens the Add dialog.
        string? url = e.Args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp");
        if (url is not null) vm.ShowAddDialog(url);
    }

    // ----- Tray, notifications, clipboard -----

    private void StartTray()
    {
        _tray = new TrayIcon();
        _tray.OpenRequested += () => _vm?.BringToFront();
        _tray.AddRequested += () => _vm?.AddFromTray();
        _tray.PauseAllRequested += () => _vm?.PauseAll();
        _tray.ExitRequested += ExitApp;

        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trayTimer.Tick += (_, _) => _tray?.SetStatus(_vm?.TrayStatus);
        _trayTimer.Start();
    }

    private void StartClipboardWatcher(Window window, AppSettings settings)
    {
        try
        {
            _clipboard = new ClipboardWatcher(window) { Enabled = settings.WatchClipboard };
            _clipboard.LinksCopied += OnLinksCopied;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard watcher not available: {ex.Message}");
        }
    }

    private void OnLinksCopied(IReadOnlyList<Uri> links)
    {
        // Copying inside our own windows (or while a dialog of ours is open) is not a download wish.
        if (Windows.OfType<Window>().Any(w => w.IsActive || (w.IsVisible && w != MainWindow && w is not ClipboardToast && w is not CompletionWindow)))
            return;
        ClipboardToast.ShowFor(links, l => _vm?.HandleCopiedLinks(l));
    }

    /// <summary>Settings were saved: refresh what lives outside the view model.</summary>
    private void ApplySettings()
    {
        if (_service is null) return;
        if (_clipboard is not null) _clipboard.Enabled = _service.Settings.WatchClipboard;
        if (_service.Settings.KeepHistory && !_service.HistoryEnabled) _service.EnableHistory(restore: false);
        _tray?.ApplyTexts();
    }

    /// <summary>True when the user can see the main window (not hidden in the tray, not minimised).</summary>
    private bool WindowVisible => MainWindow is { IsVisible: true } w && w.WindowState != WindowState.Minimized;

    private void OnJobCompleted(DownloadJob job)
    {
        if (_service is null || job.FilePath is null) return;
        if (WindowVisible)
        {
            if (_service.Settings.ShowCompletionDialog) ShowCompletion(job);
        }
        else if (_service.Settings.ShowNotifications)
        {
            _tray?.Notify(Loc.Get("DownloadFinished"), System.IO.Path.GetFileName(job.FilePath), onClick: () =>
            {
                _vm?.BringToFront();
                ShowCompletion(job);
            });
        }
    }

    private void OnJobFailed(DownloadJob job)
    {
        if (_service is null || WindowVisible || !_service.Settings.ShowNotifications) return;
        string name = job.FileName ?? job.Request.Url.Segments.LastOrDefault() ?? job.Request.Url.Host;
        _tray?.Notify(Loc.Get("DownloadFailed"), $"{name}\n{job.Error}", error: true, onClick: () => _vm?.BringToFront());
    }

    /// <summary>The close button hides to the tray (when enabled); otherwise it quits.</summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _service is null) return;
        if (!_service.Settings.CloseToTray || _tray is null)
        {
            // Let this close finish, then end the app.
            _exiting = true;
            Dispatcher.InvokeAsync(() => Shutdown());
            return;
        }
        e.Cancel = true;
        MainWindow!.Hide();
        if (!_service.Settings.TrayHintShown)
        {
            _service.Settings.TrayHintShown = true;
            SettingsStore.Save(_service.Settings);
            _tray.Notify(Loc.Get("AppTitle"), Loc.Get("TrayHint"));
        }
    }

    /// <summary>Really quits (tray menu "Exit", the after-queue action, or close with tray mode off).</summary>
    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true; // Windows is logging off: don't hide, just close
        base.OnSessionEnding(e);
    }

    /// <summary>A few seconds after start, asks GitHub for a newer release (reads the public release list only).</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            if (_service is null || _vm is null) return;
            using var http = HttpClientFactory.CreateApiClient(_service.Settings);
            var info = await new UpdateChecker(http).CheckAsync();
            if (info is null) return;
            _vm.AvailableUpdate = info;
            if (!WindowVisible)
                _tray?.Notify(Loc.Get("AppTitle"), string.Format(Loc.Get("UpdateAvailable"), info.Version), onClick: () => _vm?.BringToFront());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}"); // offline or GitHub unreachable: try next start
        }
    }

    /// <summary>Registers the extension's host (silently) and listens for links from the browser and from second instances.</summary>
    private void StartBrowserIntegration(AppSettings settings)
    {
        if (settings.BrowserIntegration && BrowserIntegration.HostExists && !BrowserIntegration.IsRegistered)
            BrowserIntegration.Register(settings.ExtraExtensionIds);

        _pipe = new AppPipeServer { MessageReceived = OnPipeMessage };
        _pipe.Start();
    }

    /// <summary>Runs on a pipe thread: answer at once, open windows on the UI thread.</summary>
    private BrowserReply OnPipeMessage(BrowserMessage message)
    {
        if (_vm is null) return BrowserReply.Failure("Starting");
        switch (message.Type)
        {
            case "ping":
                return BrowserReply.Success();
            case "show":
                Dispatcher.InvokeAsync(_vm.BringToFront);
                return BrowserReply.Success();
            case "download":
            case "batch":
                if (_service is { Settings.BrowserIntegration: false } && message.Type == "download" && message.Referrer is not null)
                    return BrowserReply.Failure("Browser capture is turned off in Smart Download Manager");
                if (message.ValidLinks().Count == 0) return BrowserReply.Failure("No valid link");
                Dispatcher.InvokeAsync(() => _vm.HandleBrowserMessage(message));
                return BrowserReply.Success();
            default:
                return BrowserReply.Failure($"Unknown message type '{message.Type}'");
        }
    }

    private void ShowCompletion(DownloadJob job)
    {
        if (_service is null || job.FilePath is null) return;
        var vm = new CompletionViewModel(_service, job);
        var window = new CompletionWindow { DataContext = vm, Owner = MainWindow };
        vm.CloseRequested += window.Close;
        window.Show(); // not modal: several downloads may finish together
    }

    private void AfterScheduledQueue(AfterQueueAction action)
    {
        switch (action)
        {
            case AfterQueueAction.Sleep:
                SetSuspendState(false, false, false);
                break;
            case AfterQueueAction.Shutdown:
                // Windows shows its own 60-second warning, which the user can cancel with "shutdown /a".
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false });
                break;
            case AfterQueueAction.ExitApp:
                ExitApp();
                break;
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _trayTimer?.Stop();
        _tray?.Dispose();
        _clipboard?.Dispose();
        _pipe?.Dispose();
        if (_service is not null)
        {
            // Saves the list (running downloads continue next start), then stops them so each keeps its
            // .sdmstate and resumes where it left off.
            _service.Shutdown();
            _service.Dispose();
        }
        base.OnExit(e);
    }
}
