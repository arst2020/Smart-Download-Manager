using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using SDM.App.Localization;
using SDM.Core;
using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>
/// The "Add download" popup: probes the link, shows what it is and where the advisor suggests saving it,
/// and lets the user pick connections, folder and naming before the download starts.
/// </summary>
public sealed class AddDownloadViewModel : ObservableObject
{
    private readonly SmartDownloadService _service;
    private CancellationTokenSource? _cts;
    private SaveSuggestion? _suggestion;
    private string _url = "";
    private string _fileName = "";
    private string _folder = "";
    private bool _analyzed;
    private bool _renameToStandard;
    private bool _rememberRule;
    private int _connections;
    private string _status = "";
    private string _warning = "";
    private bool _isBusy;
    private FolderOption? _selectedOption;

    public AddDownloadViewModel(SmartDownloadService service)
    {
        _service = service;
        _folder = service.Settings.DownloadDirectory;
        _renameToStandard = service.Settings.RenameToStandard;
        _connections = ConnectionGate.Choices.Contains(service.Settings.MaxSegments) ? service.Settings.MaxSegments : 32;
        AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => !IsBusy && IsValidUrl);
        BrowseCommand = new RelayCommand(Browse);
        PickOptionCommand = new RelayCommand(p => { if (p is FolderOption o) SelectedOption = o; });
    }

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand PickOptionCommand { get; }

    /// <summary>The user clicked "Schedule…" instead of "Start".</summary>
    public bool Scheduled { get; set; }

    public string? PosterUrl => _suggestion?.Media.PosterUrl;

    public bool HasPoster => PosterUrl is not null;

    public string KindLabel => _suggestion is null ? "" : Loc.Get("Kind_" + _suggestion.Media.Kind);

    public string SizeText { get; private set; } = "";

    public string ConfidenceText => _suggestion is null
        ? ""
        : string.Format(Loc.Get("DetectedLine"), _suggestion.Media.Source, (int)Math.Round(_suggestion.Media.Confidence * 100));

    /// <summary>"Emby library · TV shows", a rule name, or the folder kind.</summary>
    public string DestinationLabel => _suggestion switch
    {
        null => "",
        { Library: { } lib } => $"{string.Format(Loc.Get("LibraryOf"), lib.Server)} · {lib.Name}",
        { RuleName: { } rule } => $"{Loc.Get("Rule")}: {rule}",
        _ => Loc.Get("Kind_" + _suggestion.Media.Kind)
    };

    public string DestinationBadge => _suggestion?.Library?.Server[..1] ?? (_suggestion?.RuleName is not null ? "★" : "⌂");

    public bool IsLibrary => _suggestion?.Library is not null;

    public IReadOnlyList<FolderOption> Alternatives => _suggestion?.Alternatives ?? Array.Empty<FolderOption>();

    public string RememberText => _suggestion?.Media.Title is { } t ? string.Format(Loc.Get("RememberTitle"), t) : "";

    public IReadOnlyList<int> ConnectionChoices => ConnectionGate.Choices;

    public int Connections { get => _connections; set => Set(ref _connections, value); }

    public string Url
    {
        get => _url;
        set { if (Set(ref _url, value)) { OnPropertyChanged(nameof(IsValidUrl)); _analyzed = false; } }
    }

    public bool IsValidUrl =>
        Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    public string Folder { get => _folder; set => Set(ref _folder, value); }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    /// <summary>"Checking…", an error, or empty.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>A problem with AI / TMDB / Emby; the suggestion still works without them.</summary>
    public string Warning { get => _warning; private set => Set(ref _warning, value); }

    public bool HasSuggestion => _suggestion is not null;

    /// <summary>"Dark (2017) · S01E03".</summary>
    public string Title => _suggestion?.Describe() ?? "";

    /// <summary>"Series · 28.6 MB".</summary>
    public string Details { get; private set; } = "";

    /// <summary>"Detected by: local + TMDB · Library: Emby – TV Shows".</summary>
    public string Source { get; private set; } = "";

    public string Reason => _suggestion?.Media.Reason ?? "";

    public IReadOnlyList<FolderOption> FolderOptions { get; private set; } = Array.Empty<FolderOption>();

    public FolderOption? SelectedOption
    {
        get => _selectedOption;
        set { if (Set(ref _selectedOption, value) && value is not null) Folder = Resolve(value.Folder); }
    }

    public bool CanRename => _suggestion?.StandardFileName is { } std && std != _suggestion.OriginalFileName;

    public string StandardName => _suggestion?.StandardFileName ?? "";

    public string OriginalName => _suggestion?.OriginalFileName ?? "";

    public bool RenameToStandard
    {
        get => _renameToStandard;
        set
        {
            if (!Set(ref _renameToStandard, value) || _suggestion is null) return;
            FileName = value && CanRename ? StandardName : OriginalName;
        }
    }

    public bool CanRemember => _suggestion?.Media is { IsVideo: true, Title: not null } && _suggestion.RuleName is null;

    public bool RememberRule { get => _rememberRule; set => Set(ref _rememberRule, value); }

    public async Task AnalyzeAsync()
    {
        if (!IsValidUrl) { Status = Loc.Get("InvalidUrl"); return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        IsBusy = true;
        Status = Loc.Get("Analyzing");
        Warning = "";
        try
        {
            var (probe, s) = await _service.SuggestAsync(new Uri(Url.Trim()), null, _cts.Token);
            _suggestion = s;
            _analyzed = true;

            string size = probe.Length is long l ? FileNameHelper.FormatSize(l) : "?";
            SizeText = size;
            Details = $"{Loc.Get("Kind_" + s.Media.Kind)} · {size}";
            Source = $"{Loc.Get("DetectedBy")}: {s.Media.Source}"
                     + (s.Library is { } lib ? $" · {Loc.Get("Library")}: {lib.Server} – {lib.Name}" : "")
                     + (s.RuleName is { } rule ? $" · {Loc.Get("Rule")}: {rule}" : "");

            Folder = Resolve(s.Folder);
            FolderOptions = new[] { new FolderOption("★ " + Loc.Get("Suggestion"), s.Folder) }.Concat(s.Alternatives).ToList();
            _selectedOption = FolderOptions[0];
            FileName = RenameToStandard && CanRename ? StandardName : OriginalName;
            Warning = _service.LastSuggestWarning;
            Status = "";
            OnPropertyChanged(string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>What to pass to the service when the user clicks Start.</summary>
    public AddOptions BuildOptions()
    {
        string? name = string.IsNullOrWhiteSpace(FileName) ? null : FileNameHelper.Sanitize(FileName.Trim());
        // Without a suggestion (not analysed), the service decides after downloading.
        return new AddOptions
        {
            Folder = _analyzed ? Folder.Trim() : null,
            FileName = name,
            Connections = Connections,
            Scheduled = Scheduled,
        };
    }

    /// <summary>The rule to add when "Always save this title here" is ticked.</summary>
    public UserRule? BuildRule()
    {
        if (!RememberRule || _suggestion?.Media.Title is not { } title) return null;
        bool series = _suggestion.Media.Kind == MediaKind.Series;
        // For series remember the show folder (the parent of "Season NN"), so later seasons land next to it.
        string target = series ? Path.GetDirectoryName(Folder.TrimEnd('\\', '/')) ?? Folder : Folder;
        return new UserRule
        {
            Name = title,
            Keywords = { title },
            TargetFolder = target,
            SeasonSubfolders = series,
        };
    }

    private string Resolve(string folder) => Path.GetFullPath(Path.Combine(_service.Settings.DownloadDirectory, folder));

    private void Browse()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(Folder) ? Folder : null };
        if (dialog.ShowDialog() == true)
        {
            Folder = dialog.FolderName;
            _analyzed = true; // an explicit folder counts as a decision
        }
    }
}
