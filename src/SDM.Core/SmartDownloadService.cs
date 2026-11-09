using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Media.Ai;
using SDM.Core.Models;
using SDM.Core.Settings;

namespace SDM.Core;

/// <summary>Options chosen in the "Add download" popup.</summary>
public sealed record AddOptions
{
    /// <summary>Destination folder; null = decide automatically after the download.</summary>
    public string? Folder { get; init; }

    /// <summary>Final file name (e.g. the standard name); null = keep the server's name.</summary>
    public string? FileName { get; init; }

    /// <summary>1, 4, 8, 16 or 32; null = settings default.</summary>
    public int? Connections { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? PageTitle { get; init; }
}

/// <summary>
/// Facade used by the WPF app and the CLI: queue + downloader + media advisor + file mover.
/// </summary>
public sealed class SmartDownloadService : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _apiHttp;
    private readonly RateLimiter _globalLimiter = new();

    public AppSettings Settings { get; private set; }
    public DownloadQueue Queue { get; }
    public ClassifierPipeline Pipeline { get; }
    public MediaAdvisor Advisor { get; }
    public FileMover Mover { get; } = new();

    public SmartDownloadService(AppSettings settings, HttpClient? http = null, Func<AiSettings, ILlmClient?>? llmFactory = null)
    {
        Settings = settings;
        _http = http ?? HttpClientFactory.Create(settings);
        _apiHttp = HttpClientFactory.CreateApiClient(settings);
        _globalLimiter.BytesPerSecond = settings.GlobalSpeedLimitKBps * 1024;

        Pipeline = new ClassifierPipeline(new IClassifier[]
        {
            new UserRuleClassifier(() => Settings.Rules),
            new EpisodeClassifier(() => Settings.EffectiveSeriesDirectory),
            new TypeClassifier(c => Settings.FolderForCategory(c)),
        });
        Advisor = new MediaAdvisor(() => Settings, _apiHttp, llmFactory);
        Queue = new DownloadQueue(RunJobAsync, settings.MaxConcurrentDownloads);
    }

    /// <summary>Applies changed settings (speed limit, concurrency, rules, AI...) to running components.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
        _globalLimiter.BytesPerSecond = settings.GlobalSpeedLimitKBps * 1024;
        Queue.MaxConcurrent = settings.MaxConcurrentDownloads;
    }

    public DownloadJob Add(Uri url, AddOptions? options = null)
    {
        options ??= new AddOptions();
        var request = new DownloadRequest
        {
            Url = url,
            Directory = Settings.DownloadDirectory,
            FileName = options.FileName,
            MaxSegments = Math.Clamp(options.Connections ?? Settings.MaxSegments, 1, 32),
            MaxRetries = Settings.MaxRetries,
            Headers = options.Headers ?? new Dictionary<string, string>(),
            PageTitle = options.PageTitle,
        };
        return Queue.Enqueue(new DownloadJob(request) { ForcedFolder = options.Folder });
    }

    /// <summary>Probes the URL and asks the advisor where it should go (used by the popup).</summary>
    public async Task<(ProbeResult Probe, SaveSuggestion Suggestion)> SuggestAsync(
        Uri url, string? pageTitle = null, CancellationToken ct = default)
    {
        var probe = await HttpProbe.ProbeAsync(_http, url, null, ct).ConfigureAwait(false);
        var suggestion = await Advisor.SuggestAsync(
            new SuggestRequest(probe.FileName ?? "download", url, probe.ContentType, probe.Length, pageTitle), ct)
            .ConfigureAwait(false);
        return (probe, suggestion);
    }

    /// <summary>Moves a job's file back to the download directory.</summary>
    public bool UndoMove(DownloadJob job)
    {
        if (job.Move is not { } move || !Mover.Undo(move)) return false;
        job.FilePath = move.From;
        job.Move = null;
        job.Raise();
        return true;
    }

    public Task<ClassificationResult> ClassifyAsync(ClassificationContext context, CancellationToken ct = default) =>
        Pipeline.ClassifyAsync(context, ct);

    private async Task RunJobAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var downloader = new SegmentedDownloader(_http, new[] { _globalLimiter });
        DownloadResult result = await downloader.DownloadAsync(job.Request, progress, ct).ConfigureAwait(false);
        job.FilePath = result.FilePath;
        job.FileName = Path.GetFileName(result.FilePath);

        job.Status = DownloadStatus.Organizing;
        job.Raise();

        string? newName = null;
        ClassificationResult classification;
        if (job.ForcedFolder is { } forced)
        {
            // The user confirmed folder (and name) in the popup.
            classification = new ClassificationResult("Manual", forced, 1.0, "user");
        }
        else
        {
            var suggestion = await Advisor.SuggestAsync(new SuggestRequest(job.FileName, job.Request.Url,
                result.Probe.ContentType, result.Size, job.Request.PageTitle), ct).ConfigureAwait(false);
            classification = new ClassificationResult(suggestion.RuleName ?? suggestion.Media.Kind.ToString(),
                suggestion.Folder, suggestion.Media.Confidence, suggestion.Media.Source, null, suggestion.Describe());
            if (Settings.RenameToStandard && job.Request.FileName is null) newName = suggestion.StandardFileName;
        }
        job.Classification = classification;

        if (Settings.AutoOrganize || job.ForcedFolder is not null)
        {
            var move = Mover.Move(result.FilePath, classification, Settings.DownloadDirectory, newName);
            if (move is not null)
            {
                job.Move = move;
                job.FilePath = move.To;
                job.FileName = Path.GetFileName(move.To);
            }
            try { await Advisor.NotifyFileSavedAsync(job.FilePath!, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* a library scan is best effort */ }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _apiHttp.Dispose();
    }
}
