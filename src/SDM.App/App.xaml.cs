using System.Windows;
using SDM.App.Localization;
using SDM.App.ViewModels;
using SDM.App.Views;
using SDM.Core;
using SDM.Core.Settings;

namespace SDM.App;

public partial class App : Application
{
    private SmartDownloadService? _service;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings = SettingsStore.Load();
        Loc.Instance.SetLanguage(settings.Language);

        _service = new SmartDownloadService(settings);
        var vm = new MainViewModel(_service);
        var window = new MainWindow { DataContext = vm };
        vm.Owner = window;
        MainWindow = window;
        window.Show();

        // "SmartDownloadManager.exe <url>" opens the Add dialog (used by shortcuts and, later, the browser extension).
        string? url = e.Args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out _));
        if (url is not null) vm.ShowAddDialog(url);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_service is not null)
        {
            // Pause everything so partial downloads keep their state and resume next time.
            foreach (var job in _service.Queue.Jobs) _service.Queue.Pause(job);
            _service.Dispose();
        }
        base.OnExit(e);
    }
}
