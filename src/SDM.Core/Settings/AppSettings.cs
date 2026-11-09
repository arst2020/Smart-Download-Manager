using System.Net;
using System.Text.Json;
using SDM.Core.Classification;
using SDM.Core.Media;
using SDM.Core.Media.Ai;
using SDM.Core.Scheduling;

namespace SDM.Core.Settings;

public enum AppTheme
{
    System,
    Dark,
    Light
}

public sealed class AppSettings
{
    /// <summary>Dark, Light, or follow the Windows setting.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Show the "download finished" window (Run / Open folder / Close).</summary>
    public bool ShowCompletionDialog { get; set; } = true;

    public ScheduleSettings Schedule { get; set; } = new();

    /// <summary>Capture downloads from the browser extension (the extension has its own switch too).</summary>
    public bool BrowserIntegration { get; set; } = true;

    /// <summary>Extra Chrome extension IDs allowed to talk to the app (e.g. a Web Store build).</summary>
    public List<string> ExtraExtensionIds { get; set; } = new();

    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SDM");

    /// <summary>Root folder for TV series; empty = "&lt;DownloadDirectory&gt;\Series".</summary>
    public string SeriesDirectory { get; set; } = "";

    public int MaxConcurrentDownloads { get; set; } = 3;
    /// <summary>Default connections per download: 1, 4, 8, 16 or 32 (can be changed per download).</summary>
    public int MaxSegments { get; set; } = 32;
    public int MaxRetries { get; set; } = 5;

    /// <summary>Root folder for movies when no media server library is set; empty = "&lt;DownloadDirectory&gt;\Movies".</summary>
    public string MoviesDirectory { get; set; } = "";

    /// <summary>Rename media to the Emby/Jellyfin standard (e.g. "Dark - S01E03.mkv"). Can be changed per download.</summary>
    public bool RenameToStandard { get; set; } = true;

    /// <summary>AI used to identify downloads.</summary>
    public AiSettings Ai { get; set; } = new();

    /// <summary>Ask the AI for every download, not only when the local parser is unsure.</summary>
    public bool AiAlways { get; set; }

    /// <summary>TMDB v3 API key or v4 Read Access Token; empty = TMDB off.</summary>
    public string TmdbApiKey { get; set; } = "";

    public string TmdbLanguage { get; set; } = "en-US";

    public MediaServerSettings MediaServer { get; set; } = new();

    /// <summary>Global limit in KB/s; 0 = unlimited.</summary>
    public long GlobalSpeedLimitKBps { get; set; }

    /// <summary>"fa", "en" or "de".</summary>
    public string Language { get; set; } = "fa";

    /// <summary>Move finished files to their classified folder automatically.</summary>
    public bool AutoOrganize { get; set; } = true;

    /// <summary>e.g. "http://127.0.0.1:8080" or "socks5://127.0.0.1:1080"; empty = system proxy.</summary>
    public string ProxyUrl { get; set; } = "";

    /// <summary>
    /// Sent to download servers. A normal browser string, because many file hosts and CDNs reject
    /// unknown clients (often with 403 or 503) — the same choice other download managers make.
    /// </summary>
    public string UserAgent { get; set; } = DefaultUserAgent;

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Optional renames of category folders, e.g. "Video" -> "فیلم".</summary>
    public Dictionary<string, string> CategoryFolders { get; set; } = new();

    public List<UserRule> Rules { get; set; } = new();

    public string EffectiveSeriesDirectory =>
        string.IsNullOrWhiteSpace(SeriesDirectory) ? Path.Combine(DownloadDirectory, "Series") : SeriesDirectory;

    public string EffectiveMoviesDirectory =>
        string.IsNullOrWhiteSpace(MoviesDirectory) ? Path.Combine(DownloadDirectory, "Movies") : MoviesDirectory;

    public string FolderForCategory(string category) =>
        CategoryFolders.TryGetValue(category, out var f) && !string.IsNullOrWhiteSpace(f) ? f : category;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, // "Claude", not 1
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDM", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
                // Version 0.1 saved its own name as user agent, which some servers reject: upgrade it.
                if (string.IsNullOrWhiteSpace(loaded.UserAgent) || loaded.UserAgent.StartsWith("SmartDownloadManager/", StringComparison.Ordinal))
                    loaded.UserAgent = AppSettings.DefaultUserAgent;
                return loaded;
            }
        }
        catch (JsonException) { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

public static class HttpClientFactory
{
    public static HttpClient Create(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            // Never decompress: Range offsets must refer to the bytes on the wire.
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
        {
            handler.Proxy = new WebProxy(settings.ProxyUrl); // http://, https://, socks4://, socks5://
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
        return client;
    }

    /// <summary>Client for AI / TMDB / media server calls: short timeout, same proxy.</summary>
    public static HttpClient CreateApiClient(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
        {
            handler.Proxy = new WebProxy(settings.ProxyUrl) { BypassProxyOnLocal = true }; // Emby/Ollama on localhost
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
        return client;
    }
}
