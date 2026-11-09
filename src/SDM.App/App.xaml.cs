using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using SDM.App.Localization;
using SDM.App.Themes;
using SDM.App.ViewModels;
using SDM.App.Views;
using SDM.Core;
using SDM.Core.Browser;
using SDM.Core.Engine;
using SDM.Core.Scheduling;
using SDM.Core.Settings;

namespace SDM.App;

public partial class App : Application
{
    private SmartDownloadService? _service;
    private AppPipeServer? _pipe;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings = SettingsStore.Load();
        Loc.Instance.SetLanguage(settings.Language);
        ThemeManager.Apply(settings.Theme);

        _service = new SmartDownloadService(settings);
        _service.Queue.JobCompleted += job => Dispatcher.InvokeAsync(() => ShowCompletion(job));
        _service.ScheduledQueueFinished += action => Dispatcher.InvokeAsync(() => AfterScheduledQueue(action));

        var vm = new MainViewModel(_service);
        _vm = vm;
        var window = new MainWindow { DataContext = vm };
        vm.Owner = window;
        MainWindow = window;
        window.Show();

        StartBrowserIntegration(settings);

        // "SmartDownloadManager.exe <url>" opens the Add dialog.
        string? url = e.Args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp");
        if (url is not null) vm.ShowAddDialog(url);
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
        if (_service is null || !_service.Settings.ShowCompletionDialog || job.FilePath is null) return;
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
                Shutdown();
                break;
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    protected override void OnExit(ExitEventArgs e)
    {
        _pipe?.Dispose();
        if (_service is not null)
        {
            // Pause everything so partial downloads keep their state and resume next time.
            foreach (var job in _service.Queue.Jobs) _service.Queue.Pause(job);
            _service.Dispose();
        }
        base.OnExit(e);
    }
}
