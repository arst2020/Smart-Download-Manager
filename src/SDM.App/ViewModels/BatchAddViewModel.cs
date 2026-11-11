using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using SDM.App.Localization;
using SDM.Core;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Models;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>One link in the batch window, filled in as its probe and suggestion arrive.</summary>
public sealed class BatchItemViewModel : ObservableObject
{
    private bool _include = true;
    private string _title;
    private string _destination = "";
    private string _size = "";
    private string _kindKey = "Other";
    private string _badge = "…";
    private string _tag = "";
    private bool _isBusy = true;

    public BatchItemViewModel(Uri url)
    {
        Url = url;
        _title = FileNameHelper.FromUrl(url) ?? url.Host;
        _tag = Loc.Get("Analyzing");
    }

    public Uri Url { get; }
    public SaveSuggestion? Suggestion { get; private set; }
    public long? Bytes { get; private set; }

    public bool Include { get => _include; set => Set(ref _include, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Destination { get => _destination; private set => Set(ref _destination, value); }
    public string Size { get => _size; private set => Set(ref _size, value); }
    public string KindKey { get => _kindKey; private set => Set(ref _kindKey, value); }
    public string Badge { get => _badge; private set => Set(ref _badge, value); }
    public string Tag { get => _tag; private set => Set(ref _tag, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    internal void Fill(ProbeResult probe, SaveSuggestion s, string downloadDir)
    {
        Suggestion = s;
        Bytes = probe.Length;
        Title = s.Describe();
        Destination = Path.GetFullPath(Path.Combine(downloadDir, s.Folder));
        Size = probe.Length is long l ? FileNameHelper.FormatSize(l) : "?";
        KindKey = s.Media.Kind switch
        {
            MediaKind.Series => "Series", MediaKind.Movie => "Movie", MediaKind.Music => "Music",
            MediaKind.Software or MediaKind.Archive => "Software", MediaKind.Document => "Doc", _ => "Other"
        };
        Badge = s.Media.Kind switch
        {
            MediaKind.Series => "TV", MediaKind.Movie => "M", MediaKind.Music => "♪",
            _ => Path.GetExtension(s.OriginalFileName).TrimStart('.').ToUpperInvariant() is { Length: > 0 and <= 4 } e ? e : "FILE"
        };
        Tag = Loc.Get("Kind_" + s.Media.Kind);
        IsBusy = false;
    }

    internal void Fail(string message)
    {
        Tag = Loc.Get("Status_Failed");
        Destination = message;
        Include = false;
        IsBusy = false;
    }
}

/// <summary>The "add many links" window: each pasted link is probed and classified in parallel.</summary>
public sealed class BatchAddViewModel : ObservableObject
{
    private readonly SmartDownloadService _service;
    private int _connections;
    private int _concurrent;
    private bool _renameToStandard;
    private string _summary = "";

    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly string? _pageTitle;

    public BatchAddViewModel(SmartDownloadService service, LinkExtractor.Result links,
        IReadOnlyDictionary<string, string>? headers = null, string? pageTitle = null)
    {
        _service = service;
        _headers = headers;
        _pageTitle = pageTitle;
        _connections = ConnectionGate.Choices.Contains(service.Settings.MaxSegments) ? service.Settings.MaxSegments : 16;
        _concurrent = service.Settings.MaxConcurrentDownloads;
        _renameToStandard = service.Settings.RenameToStandard;
        Duplicates = links.Duplicates;
        foreach (var url in links.Links) Items.Add(new BatchItemViewModel(url));
        foreach (var item in Items) item.PropertyChanged += (_, _) => UpdateSummary();

        SelectAllCommand = new RelayCommand(() =>
        {
            bool all = Items.All(i => i.Include);
            foreach (var i in Items) i.Include = !all;
        });
        PasteAgainCommand = new RelayCommand(PasteAgain);
        UpdateSummary();
        _ = AnalyzeAllAsync(Items.ToList());
    }

    public ObservableCollection<BatchItemViewModel> Items { get; } = new();
    public int Duplicates { get; private set; }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand PasteAgainCommand { get; }

    public IReadOnlyList<int> ConnectionChoices => ConnectionGate.Choices;
    public IReadOnlyList<int> ConcurrentChoices { get; } = new[] { 1, 3, 5 };

    public int Connections { get => _connections; set => Set(ref _connections, value); }

    private IReadOnlyList<QueueOption>? _queues;
    public IReadOnlyList<QueueOption> QueueOptions => _queues ??= QueueOption.From(_service);
    public bool HasQueues => QueueOptions.Count > 1;
    public string QueueId { get; set; } = SDM.Core.Engine.DownloadQueue.MainQueueId;
    public int Concurrent { get => _concurrent; set => Set(ref _concurrent, value); }
    public bool RenameToStandard { get => _renameToStandard; set => Set(ref _renameToStandard, value); }

    /// <summary>"Schedule…" was pressed.</summary>
    public bool Scheduled { get; set; }

    /// <summary>"Add to queue" was pressed: add paused.</summary>
    public bool AddPaused { get; set; }

    public string Header => string.Format(Loc.Get("BatchHeader"), Items.Count, Duplicates);

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string StartText => string.Format(Loc.Get("StartN"), Items.Count(i => i.Include));

    public string SelectAllText => string.Format(Loc.Get("SelectAllN"), Items.Count(i => i.Include));

    private void UpdateSummary()
    {
        long total = Items.Where(i => i.Include).Sum(i => i.Bytes ?? 0);
        Summary = $"{Loc.Get("TotalSize")}: {FileNameHelper.FormatSize(total)}";
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(SelectAllText));
    }

    private async Task AnalyzeAllAsync(IReadOnlyList<BatchItemViewModel> items)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                // The page title belongs to the whole page: with several links it would push them all into one
                // show's folder, so it is only used when there is a single link.
                string? title = Items.Count == 1 ? _pageTitle : null;
                var (probe, s) = await _service.SuggestAsync(item.Url, title, cts.Token, _headers);
                Application.Current?.Dispatcher.Invoke(() => item.Fill(probe, s, _service.Settings.DownloadDirectory));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                Application.Current?.Dispatcher.Invoke(() => item.Fail(ex.Message));
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private void PasteAgain()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return; }
        var links = LinkExtractor.Extract(text);
        var known = Items.Select(i => i.Url.AbsoluteUri).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<BatchItemViewModel>();
        foreach (var url in links.Links)
        {
            if (!known.Add(url.AbsoluteUri)) { Duplicates++; continue; }
            var item = new BatchItemViewModel(url);
            item.PropertyChanged += (_, _) => UpdateSummary();
            Items.Add(item);
            added.Add(item);
        }
        OnPropertyChanged(nameof(Header));
        UpdateSummary();
        _ = AnalyzeAllAsync(added);
    }

    /// <summary>Adds the ticked links to the queue with the chosen options.</summary>
    public void Commit()
    {
        _service.Queue.MaxConcurrent = Concurrent;
        bool batch1 = Items.Count(i => i.Include) == 1;
        var batch = Items.Where(i => i.Include).Select(i =>
        {
            var s = i.Suggestion;
            string? name = s is not null && RenameToStandard ? s.StandardFileName : null;
            string? folder = s is null ? null : Path.GetFullPath(Path.Combine(_service.Settings.DownloadDirectory, s.Folder));
            return (i.Url, new AddOptions
            {
                Folder = folder, FileName = name, Connections = Connections, Scheduled = Scheduled, StartPaused = AddPaused, QueueId = QueueId,
                Headers = _headers, PageTitle = batch1 ? _pageTitle : null
            });
        }).ToList();
        _service.AddMany(batch);
    }
}
