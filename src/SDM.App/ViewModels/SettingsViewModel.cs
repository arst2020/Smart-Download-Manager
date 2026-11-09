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

public sealed record ChoiceOption<T>(T Value, string Name);

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
    }

    public IReadOnlyList<LanguageOption> Languages { get; } =
        Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name)).ToList();

    public IReadOnlyList<int> ConnectionChoices => ConnectionGate.Choices;

    public IReadOnlyList<ChoiceOption<AiProvider>> AiProviders { get; } = new[]
    {
        new ChoiceOption<AiProvider>(AiProvider.None, Loc.Get("AiNone")),
        new ChoiceOption<AiProvider>(AiProvider.Claude, "Claude (Anthropic)"),
        new ChoiceOption<AiProvider>(AiProvider.OpenAI, "OpenAI (ChatGPT)"),
        new ChoiceOption<AiProvider>(AiProvider.Gemini, "Google Gemini"),
        new ChoiceOption<AiProvider>(AiProvider.Ollama, "Ollama (local, free)"),
        new ChoiceOption<AiProvider>(AiProvider.OpenAICompatible, "OpenAI-compatible (LM Studio, OpenRouter…)"),
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
