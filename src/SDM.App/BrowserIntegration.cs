using System.IO;
using Microsoft.Win32;
using SDM.Core.Browser;

namespace SDM.App;

/// <summary>
/// Registers the native messaging host for Chrome, Edge and Firefox (per user, no admin rights):
/// writes the host manifests to %LocalAppData%\SDM\NativeHost and points the browsers' registry keys at them.
/// </summary>
public static class BrowserIntegration
{
    private static readonly string[] ChromiumKeys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\",
        @"Software\Microsoft\Edge\NativeMessagingHosts\",
        @"Software\Chromium\NativeMessagingHosts\",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\",
    };

    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\";

    public static string AppFolder => AppContext.BaseDirectory;

    public static string HostExe => Path.Combine(AppFolder, "SmartDownloadManager.Host.exe");

    public static string ExtensionFolder => Path.Combine(AppFolder, "extension");

    private static string ManifestFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDM", "NativeHost");

    public static bool HostExists => File.Exists(HostExe);

    /// <summary>True when Chrome's registry key points at a manifest that names this host exe.</summary>
    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ChromiumKeys[0] + NativeHostManifest.HostName);
                return key?.GetValue(null) is string path && File.Exists(path)
                       && File.ReadAllText(path).Contains(HostExe.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Writes the manifests and registry keys. Returns null on success, else the error.</summary>
    public static string? Register(IEnumerable<string>? extraExtensionIds = null)
    {
        if (!HostExists) return $"Not found: {HostExe}";
        try
        {
            Directory.CreateDirectory(ManifestFolder);
            string chromium = Path.Combine(ManifestFolder, "chromium.json");
            string firefox = Path.Combine(ManifestFolder, "firefox.json");
            File.WriteAllText(chromium, NativeHostManifest.ForChromium(HostExe, extraExtensionIds));
            File.WriteAllText(firefox, NativeHostManifest.ForFirefox(HostExe));

            foreach (var k in ChromiumKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(k + NativeHostManifest.HostName);
                key.SetValue(null, chromium);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(FirefoxKey + NativeHostManifest.HostName))
                key.SetValue(null, firefox);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    public static void Unregister()
    {
        foreach (var k in ChromiumKeys.Append(FirefoxKey))
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(k + NativeHostManifest.HostName, throwOnMissingSubKey: false); }
            catch { /* best effort */ }
        }
    }
}
