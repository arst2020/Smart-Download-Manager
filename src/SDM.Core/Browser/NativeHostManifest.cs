using System.Text.Json;
using System.Text.Json.Nodes;

namespace SDM.Core.Browser;

/// <summary>
/// The JSON files that tell browsers which program answers the extension
/// (Windows registers their paths under HKCU\Software\{Google\Chrome|Microsoft\Edge|Mozilla}\NativeMessagingHosts).
/// </summary>
public static class NativeHostManifest
{
    public const string HostName = "com.smartdownloadmanager.host";

    /// <summary>ID of the unpacked extension; fixed by the "key" in extension/manifest.json.</summary>
    public const string ChromeExtensionId = "hlmploemhlgmkommeccngdhdpjggjcdi";

    /// <summary>Firefox add-on ID (browser_specific_settings.gecko.id in extension/firefox/manifest.json).</summary>
    public const string FirefoxExtensionId = "sdm@smartdownloadmanager.app";

    /// <summary>Chrome and Edge (also Brave, Vivaldi, Opera — they read the Chrome key).</summary>
    public static string ForChromium(string hostExePath, IEnumerable<string>? extraExtensionIds = null)
    {
        var origins = new JsonArray($"chrome-extension://{ChromeExtensionId}/");
        foreach (var id in extraExtensionIds ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(id)) origins.Add($"chrome-extension://{id.Trim()}/");

        return new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "Smart Download Manager",
            ["path"] = hostExePath,
            ["type"] = "stdio",
            ["allowed_origins"] = origins,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ForFirefox(string hostExePath) => new JsonObject
    {
        ["name"] = HostName,
        ["description"] = "Smart Download Manager",
        ["path"] = hostExePath,
        ["type"] = "stdio",
        ["allowed_extensions"] = new JsonArray(FirefoxExtensionId),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// True when the process was started by a browser as a native messaging host:
    /// Chrome passes "chrome-extension://id/", Firefox passes the manifest path and the add-on id.
    /// </summary>
    public static bool IsHostLaunch(IEnumerable<string> args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                      || a.Equals(FirefoxExtensionId, StringComparison.OrdinalIgnoreCase)
                      || a.Equals("--native-host", StringComparison.OrdinalIgnoreCase));
}
