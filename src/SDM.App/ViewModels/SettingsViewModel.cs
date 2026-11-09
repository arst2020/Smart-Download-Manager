using System.IO;
using System.Collections.ObjectModel;
using Microsoft.Win32;
using SDM.App.Localization;
using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Media.Ai;
using SDM.Core.Settings;

namespace SDM.App.ViewModels;

/// <summary>Editable row for one user rule; lists are edited as comma-separated text.</summary>
public sealed class RuleRow : ObservableObject
{
    private bool _enabled = true;
    private string _name = "";
    private string _keywords = "";
    private string _domains = "";
    private string _target = "";
    private bool _season = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Keywords { get => _keywords; set => Set(ref _keywords, value); }
    public string Domains { get => _domains; set => Set(ref _domains, value); }
    public string TargetFolder { get => _target; set => Set(ref _target, value); }
    public bool SeasonSubfolders { get => _season; set => Set(ref _season, value); }

    // Keep rule fields this editor does not show (pattern, extensions).
    public string? Pattern { get; init; }
    public List<string> Extensions { get; init; } = new();

    public static RuleRow From(UserRule r) => new()
    {
        Enabled = r.Enabled,
        Name = r.Name,
        Keywords = string.Join(", ", r.Keywords),
        Domains = string.Join(", ", r.Domains),
        TargetFolder = r.TargetFolder,
        SeasonSubfolders = r.SeasonSubfolders,
        Pattern = r.Pattern,
        Extensions = r.Extensions.ToList(),
    };

    public UserRule ToRule() => new()
    {
        Enabled = Enabled,
        Name = string.IsNullOrWhiteSpace(Name) ? Keywords : Name.Trim(),
        Keywords = SplitList(Keywords),
        Domains = SplitList(Domains),
        TargetFolder = TargetFolder.Trim(),
        SeasonSubfolders = SeasonSubfolders,
        Pattern = Pattern,
        Extensions = Extensions,
    };

    private static List<string> SplitList(string s) =>
        s.Split(new[] { ',', '،', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public sealed record LanguageOption(string Code, string Name);

public sealed record ChoiceOption<T>(T Value, string Name)
{
    public string Note { get; init; } = "";
    public string Badge { get; init; } = "";
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _original;
    private RuleRow? _selectedRule;
    private string _downloadDirectory = "";
    private string _seriesDirectory = "";
    private string _moviesDirectory = "";
    private AiProvider _aiProvider;
    private string _aiTest = "";
    private string _tmdbTest = "";
    private string _serverTest = "";

    public SettingsViewModel(AppSettings settings)
    {
        _original = settings;
        DownloadDirectory = settings.DownloadDirectory;
        SeriesDirectory = settings.SeriesDirectory;
        MoviesDirectory = settings.MoviesDirectory;
        MaxConcurrent = settings.MaxConcurrentDownloads;
        Connections = ConnectionGate.Choices.Contains(settings.MaxSegments) ? settings.MaxSegments : 32;
        SpeedLimit = settings.GlobalSpeedLimitKBps;
        Language = settings.Language;
        AutoOrganize = settings.AutoOrganize;
        RenameToStandard = settings.RenameToStandard;
        ProxyUrl = settings.ProxyUrl;
        Theme = settings.Theme;
        ShowCompletionDialog = settings.ShowCompletionDialog;
        CloseToTray = settings.CloseToTray;
        ShowNotifications = settings.ShowNotifications;
        WatchClipboard = settings.WatchClipboard;
        KeepHistory = settings.KeepHistory;
        ResumeOnStart = settings.ResumeOnStart;
        _subtitleSearchUrl = settings.SubtitleSearchUrl;
        ScheduleSummary = settings.Schedule.Enabled
            ? $"{settings.Schedule.Start} – {settings.Schedule.Stop}"
            : Loc.Get("ScheduleOff");

        _aiProvider = settings.Ai.Provider;
        AiApiKey = settings.Ai.ApiKey;
        AiModel = settings.Ai.Model;
        AiBaseUrl = settings.Ai.BaseUrl;
        AiAlways = settings.AiAlways;
        TmdbApiKey = settings.TmdbApiKey;
        TmdbLanguage = settings.TmdbLanguage;
        ServerType = settings.MediaServer.Type;
        ServerUrl = settings.MediaServer.Url;
        ServerApiKey = settings.MediaServer.ApiKey;
        ServerRefresh = settings.MediaServer.RefreshAfterDownload;

        foreach (var r in settings.Rules) Rules.Add(RuleRow.From(r));

        BrowseDownloadCommand = new RelayCommand(() => DownloadDirectory = PickFolder(DownloadDirectory) ?? DownloadDirectory);
        BrowseSeriesCommand = new RelayCommand(() => SeriesDirectory = PickFolder(SeriesDirectory) ?? SeriesDirectory);
        BrowseMoviesCommand = new RelayCommand(() => MoviesDirectory = PickFolder(MoviesDirectory) ?? MoviesDirectory);
        BrowseRuleCommand = new RelayCommand(() =>
        {
            if (SelectedRule is { } r) r.TargetFolder = PickFolder(r.TargetFolder) ?? r.TargetFolder;
        }, () => SelectedRule is not null);
        AddRuleCommand = new RelayCommand(() =>
        {
            var row = new RuleRow { TargetFolder = Path.Combine(DownloadDirectory, "Series") };
            Rules.Add(row);
            SelectedRule = row;
        });
        DeleteRuleCommand = new RelayCommand(() => Rules.Remove(SelectedRule!), () => SelectedRule is not null);
        TestAiCommand = new RelayCommand(async () => await TestAiAsync(), () => AiProvider != AiProvider.None);
        TestTmdbCommand = new RelayCommand(async () => await TestTmdbAsync(), () => !string.IsNullOrWhiteSpace(TmdbApiKey));
        TestServerCommand = new RelayCommand(async () => await TestServerAsync(), () => ServerType != MediaServerType.None);
        OpenSchedulerCommand = new RelayCommand(() => OpenScheduler?.Invoke());
        BrowserIntegration = settings.BrowserIntegration;
        ExtraExtensionIds = string.Join(", ", settings.ExtraExtensionIds);
        RegisterBrowserCommand = new RelayCommand(() =>
        {
            string? error = SDM.App.BrowserIntegration.Register(ParseIds(ExtraExtensionIds));
            BrowserStatus = error is null ? "✔ " + Loc.Get("BrowserRegistered") : "✖ " + error;
        });
        OpenExtensionFolderCommand = new RelayCommand(() =>
        {
            if (Directory.Exists(SDM.App.BrowserIntegration.ExtensionFolder))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{SDM.App.BrowserIntegration.ExtensionFolder}\"");
        });
        CopyExtensionPathCommand = new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(SDM.App.BrowserIntegration.ExtensionFolder); } catch { /* clipboard busy */ }
        });
        BrowserStatus = !SDM.App.BrowserIntegration.HostExists
            ? "✖ " + Loc.Get("HostMissing")
            : SDM.App.BrowserIntegration.IsRegistered ? "✔ " + Loc.Get("BrowserRegistered") : Loc.Get("BrowserNotRegistered");
        PickProviderCommand = new RelayCommand(p => { if (p is ChoiceOption<AiProvider> o) AiProvider = o.Value; });
    }

    private string _browserStatus = "";

    public bool BrowserIntegration { get; set; }
    public string ExtraExtensionIds { get; set; } = "";
    public string ExtensionFolder => SDM.App.BrowserIntegration.ExtensionFolder;
    public string ExtensionId => SDM.Core.Browser.NativeHostManifest.ChromeExtensionId;
    public string BrowserStatus { get => _browserStatus; private set => Set(ref _browserStatus, value); }
    public RelayCommand RegisterBrowserCommand { get; }
    public RelayCommand OpenExtensionFolderCommand { get; }
    public RelayCommand CopyExtensionPathCommand { get; }

    private static List<string> ParseIds(string s) =>
        s.Split(new[] { ',', ';', ' ', '،' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Set by the main window: opens the scheduler window.</summary>
    public Action? OpenScheduler { get; set; }

    public RelayCommand OpenSchedulerCommand { get; }
    public RelayCommand PickProviderCommand { get; }

    public AppTheme Theme { get; set; }
    public bool ShowCompletionDialog { get; set; }
    public bool CloseToTray { get; set; }
    public bool ShowNotifications { get; set; }
    public bool WatchClipboard { get; set; }
    public bool KeepHistory { get; set; }
    public bool ResumeOnStart { get; set; }

    private string _subtitleSearchUrl;

    /// <summary>Search page opened by "Subtitles" ({query} = "Show S01E03" / "Movie 2024"). Only opened, never downloaded.</summary>
    public string SubtitleSearchUrl
    {
        get => _subtitleSearchUrl;
        set { if (Set(ref _subtitleSearchUrl, value)) { OnPropertyChanged(nameof(SubtitleExample)); OnPropertyChanged(nameof(SelectedSubtitlePreset)); } }
    }

    public IReadOnlyList<SubtitlePreset> SubtitlePresets { get; } =
        SubtitleSearch.Presets.Select(p => new SubtitlePreset(p.Name, p.Template)).ToList();

    /// <summary>Picking a preset fills the address box.</summary>
    public SubtitlePreset? SelectedSubtitlePreset
    {
        get => SubtitlePresets.FirstOrDefault(p => string.Equals(p.Template, SubtitleSearchUrl, StringComparison.OrdinalIgnoreCase));
        set { if (value is not null) SubtitleSearchUrl = value.Template; }
    }

    /// <summary>Preview of the link for a sample episode (or a warning when the address is not http/https).</summary>
    public string SubtitleExample =>
        SubtitleSearch.BuildUrl(SubtitleSearchUrl, "Dark S01E03")?.AbsoluteUri ?? Loc.Get("SubtitleUrlInvalid");
    public string ScheduleSummary { get; }

    public IReadOnlyList<LanguageOption> Languages { get; } =
        Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name)).ToList();

    public IReadOnlyList<int> ConnectionChoices => ConnectionGate.Choices;

    public IReadOnlyList<ChoiceOption<AiProvider>> AiProviders { get; } = new[]
    {
        new ChoiceOption<AiProvider>(AiProvider.Claude, "Claude") { Note = "Anthropic", Badge = "C" },
        new ChoiceOption<AiProvider>(AiProvider.OpenAI, "OpenAI") { Note = "ChatGPT", Badge = "O" },
        new ChoiceOption<AiProvider>(AiProvider.Gemini, "Gemini") { Note = "Google", Badge = "G" },
        new ChoiceOption<AiProvider>(AiProvider.Ollama, "Ollama") { Note = Loc.Get("LocalFree"), Badge = "L" },
        new ChoiceOption<AiProvider>(AiProvider.OpenAICompatible, Loc.Get("OpenAICompatible")) { Note = "LM Studio · OpenRouter", Badge = "{}" },
        new ChoiceOption<AiProvider>(AiProvider.None, Loc.Get("AiNone")) { Note = Loc.Get("LocalOnly"), Badge = "—" },
    };

    public IReadOnlyList<ChoiceOption<MediaServerType>> ServerTypes { get; } = new[]
    {
        new ChoiceOption<MediaServerType>(MediaServerType.None, Loc.Get("AiNone")),
        new ChoiceOption<MediaServerType>(MediaServerType.Emby, "Emby"),
        new ChoiceOption<MediaServerType>(MediaServerType.Jellyfin, "Jellyfin"),
    };

    // General
    public string DownloadDirectory { get => _downloadDirectory; set => Set(ref _downloadDirectory, value); }
    public string SeriesDirectory { get => _seriesDirectory; set => Set(ref _seriesDirectory, value); }
    public string MoviesDirectory { get => _moviesDirectory; set => Set(ref _moviesDirectory, value); }
    public int MaxConcurrent { get; set; }
    public int Connections { get; set; }
    public long SpeedLimit { get; set; }
    public string Language { get; set; } = "fa";
    public bool AutoOrganize { get; set; }
    public bool RenameToStandard { get; set; }
    public string ProxyUrl { get; set; } = "";

    // AI + TMDB
    public AiProvider AiProvider
    {
        get => _aiProvider;
        set
        {
            if (!Set(ref _aiProvider, value)) return;
            OnPropertyChanged(nameof(AiNeedsKey));
            OnPropertyChanged(nameof(AiDefaults));
        }
    }
    public bool AiNeedsKey => AiProvider is not (AiProvider.None or AiProvider.Ollama);
    public string AiDefaults => AiProvider == AiProvider.None ? ""
        : $"{LlmClientFactory.DefaultModel(AiProvider)}  ·  {LlmClientFactory.DefaultBaseUrl(AiProvider)}";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string AiBaseUrl { get; set; } = "";
    public bool AiAlways { get; set; }
    public string AiTestResult { get => _aiTest; private set => Set(ref _aiTest, value); }
    public string TmdbApiKey { get; set; } = "";
    public string TmdbLanguage { get; set; } = "en-US";
    public string TmdbTestResult { get => _tmdbTest; private set => Set(ref _tmdbTest, value); }

    // Media server
    public MediaServerType ServerType { get; set; }
    public string ServerUrl { get; set; } = "";
    public string ServerApiKey { get; set; } = "";
    public bool ServerRefresh { get; set; }
    public string ServerTestResult { get => _serverTest; private set => Set(ref _serverTest, value); }

    public ObservableCollection<RuleRow> Rules { get; } = new();

    public RuleRow? SelectedRule { get => _selectedRule; set => Set(ref _selectedRule, value); }

    public RelayCommand BrowseDownloadCommand { get; }
    public RelayCommand BrowseSeriesCommand { get; }
    public RelayCommand BrowseMoviesCommand { get; }
    public RelayCommand BrowseRuleCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand TestAiCommand { get; }
    public RelayCommand TestTmdbCommand { get; }
    public RelayCommand TestServerCommand { get; }

    public AppSettings ToSettings() => new()
    {
        DownloadDirectory = DownloadDirectory.Trim(),
        SeriesDirectory = SeriesDirectory.Trim(),
        MoviesDirectory = MoviesDirectory.Trim(),
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrent, 1, 20),
        MaxSegments = Math.Clamp(Connections, 1, 32),
        MaxRetries = _original.MaxRetries,
        GlobalSpeedLimitKBps = Math.Max(0, SpeedLimit),
        Language = Language,
        AutoOrganize = AutoOrganize,
        RenameToStandard = RenameToStandard,
        ProxyUrl = ProxyUrl.Trim(),
        Theme = Theme,
        ShowCompletionDialog = ShowCompletionDialog,
        CloseToTray = CloseToTray,
        ShowNotifications = ShowNotifications,
        WatchClipboard = WatchClipboard,
        KeepHistory = KeepHistory,
        ResumeOnStart = ResumeOnStart,
        TrayHintShown = _original.TrayHintShown,
        SubtitleSearchUrl = SubtitleSearch.BuildUrl(SubtitleSearchUrl, "x") is null ? SubtitleSearch.DefaultTemplate : SubtitleSearchUrl.Trim(),
        BrowserIntegration = BrowserIntegration,
        ExtraExtensionIds = ParseIds(ExtraExtensionIds),
        Schedule = _original.Schedule,
        UserAgent = _original.UserAgent,
        CategoryFolders = new(_original.CategoryFolders),
        Ai = new AiSettings { Provider = AiProvider, ApiKey = AiApiKey.Trim(), Model = AiModel.Trim(), BaseUrl = AiBaseUrl.Trim() },
        AiAlways = AiAlways,
        TmdbApiKey = TmdbApiKey.Trim(),
        TmdbLanguage = string.IsNullOrWhiteSpace(TmdbLanguage) ? "en-US" : TmdbLanguage.Trim(),
        MediaServer = new MediaServerSettings
        {
            Type = ServerType, Url = ServerUrl.Trim(), ApiKey = ServerApiKey.Trim(), RefreshAfterDownload = ServerRefresh
        },
        Rules = Rules.Where(r => !string.IsNullOrWhiteSpace(r.Keywords) || !string.IsNullOrWhiteSpace(r.Domains))
                     .Select(r => r.ToRule()).ToList(),
    };

    private async Task TestAiAsync()
    {
        AiTestResult = Loc.Get("Testing");
        var settings = ToSettings();
        using var http = HttpClientFactory.CreateApiClient(settings);
        try
        {
            var client = LlmClientFactory.Create(settings.Ai, http);
            if (client is null) { AiTestResult = Loc.Get("ApiKey") + "?"; return; }
            var info = await new LlmMediaDetector(client).DetectAsync(
                "the.office.us.s02e10.720p.web.x264.mkv", null, "video/x-matroska", 350_000_000, null, CancellationToken.None);
            AiTestResult = info is null
                ? "?"
                : $"✔ {Loc.Get("TestOk")}: {info.Kind} · {info.Title} S{info.Season:00}E{info.Episode:00}";
        }
        catch (Exception ex) { AiTestResult = "✖ " + ex.Message; }
    }

    private async Task TestTmdbAsync()
    {
        TmdbTestResult = Loc.Get("Testing");
        var settings = ToSettings();
        using var http = HttpClientFactory.CreateApiClient(settings);
        try
        {
            var match = await new TmdbClient(http, settings.TmdbApiKey, settings.TmdbLanguage)
                .FindSeriesAsync("Dark", 2017, CancellationToken.None);
            TmdbTestResult = match is null ? "?" : $"✔ {Loc.Get("TestOk")}: {match.Title} ({match.Year})";
        }
        catch (Exception ex) { TmdbTestResult = "✖ " + ex.Message; }
    }

    private async Task TestServerAsync()
    {
        ServerTestResult = Loc.Get("Testing");
        var settings = ToSettings();
        using var http = HttpClientFactory.CreateApiClient(settings);
        try
        {
            var libs = await new MediaServerClient(http, settings.MediaServer).GetLibrariesAsync(CancellationToken.None);
            ServerTestResult = $"✔ {Loc.Get("TestOk")}: " + string.Join("  ·  ", libs.Select(l =>
                $"{l.Name} [{l.Kind}] → {string.Join(", ", l.Locations)}{(l.Locations.Any(Directory.Exists) ? "" : " (not found on this PC)")}"));
        }
        catch (Exception ex) { ServerTestResult = "✖ " + ex.Message; }
    }

    private static string? PickFolder(string current)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(current) ? current : null };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed record SubtitlePreset(string Name, string Template);
