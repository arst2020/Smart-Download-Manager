using System.Diagnostics;
using SDM.Core.Browser;

// Native messaging host: browser → (stdin) → here → (named pipe) → Smart Download Manager.
// If the app is not running, it is started and the message is delivered once it listens.

string appExe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SmartDownloadManager.exe" : "SmartDownloadManager");

bool LaunchApp()
{
    if (!File.Exists(appExe)) return false;
    try
    {
        Process.Start(new ProcessStartInfo(appExe, "--from-browser") { UseShellExecute = true });
        return true;
    }
    catch
    {
        return false;
    }
}

async Task<BrowserReply> ForwardAsync(BrowserMessage message, CancellationToken ct)
{
    if (message.Type == "ping")
    {
        // The popup only wants to know whether everything is wired up; don't start the app for that.
        try { return await AppPipe.SendAsync(message, connectTimeout: TimeSpan.FromMilliseconds(500), ct: ct); }
        catch (TimeoutException) { return BrowserReply.Failure("app-not-running"); }
    }
    return await AppPipe.SendOrLaunchAsync(message, LaunchApp, ct: ct);
}

await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
try
{
    await NativeMessaging.RunHostAsync(stdin, stdout, ForwardAsync);
}
catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
{
    // Browser closed the pipe or sent garbage: nothing useful to report.
    return 1;
}
return 0;
