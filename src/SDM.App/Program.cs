using System.IO;
using SDM.Core.Browser;

namespace SDM.App;

/// <summary>
/// Entry point. Only one app runs per Windows user: a second start (e.g. "SmartDownloadManager.exe &lt;url&gt;")
/// hands its link to the running app over the pipe and exits.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, "SmartDownloadManager-" + Environment.UserName, out bool firstInstance);
        if (!firstInstance)
        {
            ForwardToRunningApp(args);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        int code = app.Run();
        GC.KeepAlive(mutex);
        return code;
    }

    private static void ForwardToRunningApp(string[] args)
    {
        string? url = args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp");
        var message = url is null ? new BrowserMessage { Type = "show" } : new BrowserMessage { Type = "download", Url = url };
        try
        {
            AppPipe.SendAsync(message, connectTimeout: TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // The first instance is still starting or hung; nothing more we can do.
        }
    }
}
