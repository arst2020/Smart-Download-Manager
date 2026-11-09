using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Media.Ai;
using SDM.Core.Models;
using SDM.Core.Scheduling;
using SDM.Core.Settings;
using SDM.Core.Util;

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

    /// <summary>Wait for the scheduler's time window instead of starting now.</summary>
    public bool Scheduled { get; init; }

    /// <summary>Queue id (<see cref="DownloadQueue.MainQueueId"/> when null).</summary>
    public string? QueueId { get; init; }

    /// <summary>Add to the list paused ("add to queue"); the user starts it later.</summary>
    public bool StartPaused { get; init; }
}

/// <summary>
/// Facade used by the WPF app and the CLI: queue + downloader + media advisor + file mover.
/// </summary>
public sealed class SmartDownloadService : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _apiHttp;
    private readonly RateLimiter _globalLimiter = new();
    private Dictionary<string, RateLimiter> _queueLimiters = new();
    private readonly Timer _schedulerTimer;
    private readonly object _scheduleLock = new();
    private bool _windowOpen;
    private bool _finishedRaised;
    private string? _historyPath;
    private Timer? _historyTimer;
    private int _historyDirty;
    private readonly object _historyLock = new();

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
        ApplyQueues();
        Queue.JobCompleted += _ => SchedulerTick(DateTime.Now);
        _schedulerTimer = new Timer(_ => SchedulerTick(DateTime.Now), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20));
    }

    /// <summary>The scheduled queue finished inside the window; the UI performs the action (sleep, shutdown...).</summary>
    public event Action<AfterQueueAction>? ScheduledQueueFinished;

    // ---------------------------------------------------------------- history

    /// <summary>Loads the saved list (if any) and keeps saving it while the app runs.</summary>
    public bool HistoryEnabled => _historyPath is not null;

    /// <param name="restore">False when the option is switched on while the app runs: start saving, keep the current list.</param>
    public void EnableHistory(string? path = null, bool restore = true)
    {
        if (_historyPath is not null) return;
        _historyPath = path ?? DownloadHistory.DefaultPath;
        if (restore) RestoreHistory(DownloadHistory.Load(_historyPath), Settings.ResumeOnStart);

        Queue.JobAdded += job => { job.Changed += _ => MarkHistoryDirty(); MarkHistoryDirty(); };
        Queue.JobRemoved += _ => MarkHistoryDirty();
        foreach (var job in Queue.Jobs) job.Changed += _ => MarkHistoryDirty();
        _historyTimer = new Timer(_ => SaveHistoryIfDirty(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>Puts saved downloads back in the list. Ones that were running continue (or wait paused).</summary>
    public void RestoreHistory(IEnumerable<DownloadRecord> records, bool resumeUnfinished)
    {
        foreach (var record in records.OrderBy(r => r.Added))
        {
            DownloadJob job;
            try { job = DownloadJob.FromRecord(record, Settings.MaxRetries); }
            catch (UriFormatException) { continue; }

            job.Status = record.Status switch
            {
                DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading or DownloadStatus.Organizing =>
                    job.IsScheduled ? DownloadStatus.Scheduled : resumeUnfinished ? DownloadStatus.Queued : DownloadStatus.Paused,
                DownloadStatus.Scheduled when IsScheduleWindowOpen => DownloadStatus.Queued,
                var s => s
            };
            Queue.Enqueue(job);
        }
    }

    public void SaveHistory()
    {
        if (_historyPath is null || !Settings.KeepHistory) return;
        lock (_historyLock)
        {
            Interlocked.Exchange(ref _historyDirty, 0);
            try { DownloadHistory.Save(_historyPath, Queue.Jobs.Select(j => j.ToRecord())); }
            catch (IOException) { Interlocked.Exchange(ref _historyDirty, 1); } // retry next tick
        }
    }

    /// <summary>
    /// Closing the app: save the list as it is (running downloads stay "running" so they continue next
    /// start), then stop them so each writes its .sdmstate, and wait briefly for that.
    /// </summary>
    public void Shutdown(TimeSpan? wait = null)
    {
        _historyTimer?.Dispose();
        SaveHistory();
        _historyPath = null; // the stops below must not overwrite the saved statuses

        var running = Queue.Jobs.Where(j => j.Status is DownloadStatus.Probing or DownloadStatus.Downloading
            or DownloadStatus.Organizing).ToList();
        foreach (var job in running) Queue.Pause(job);
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline && running.Any(j => j.Status is DownloadStatus.Probing or DownloadStatus.Downloading
                   or DownloadStatus.Organizing))
            Thread.Sleep(50);
    }

    private void MarkHistoryDirty() => Interlocked.Exchange(ref _historyDirty, 1);

    private void SaveHistoryIfDirty()
    {
        if (Volatile.Read(ref _historyDirty) == 1) SaveHistory();
    }

    /// <summary>Tests drive the scheduler with their own clock.</summary>
    internal void StopSchedulerTimer() => _schedulerTimer.Change(Timeout.Infinite, Timeout.Infinite);

    public bool IsScheduleWindowOpen
    {
        get { lock (_scheduleLock) return _windowOpen; }
    }

    public DateTime? NextScheduledStart => ScheduleWindow.NextStart(Settings.Schedule, DateTime.Now);

    /// <summary>Opens/closes the schedule window and detects the end of the scheduled queue. Public for tests.</summary>
    public void SchedulerTick(DateTime now)
    {
        var schedule = Settings.Schedule;
        bool open = ScheduleWindow.IsOpen(schedule, now);
        bool opened, closed;
        lock (_scheduleLock)
        {
            opened = open && !_windowOpen;
            closed = !open && _windowOpen;
            _windowOpen = open;
            if (opened) _finishedRaised = false;
        }

        if (opened)
        {
            ApplySpeedLimit();
            Queue.ReleaseScheduled();
        }
        else if (closed)
        {
            ApplySpeedLimit();
            if (schedule.PauseAtEnd) Queue.ParkScheduled();
        }

        if (!open) return;
        var scheduled = Queue.Jobs.Where(j => j.IsScheduled).ToList();
        bool allDone = scheduled.Count > 0 && scheduled.All(j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed
                                                                 or DownloadStatus.Canceled);
        bool raise = false;
        lock (_scheduleLock)
        {
            if (allDone && !_finishedRaised) { _finishedRaised = true; raise = true; }
        }
        if (raise) ScheduledQueueFinished?.Invoke(schedule.AfterQueue);
    }

    private void ApplySpeedLimit()
    {
        long kbps = IsScheduleWindowOpen && Settings.Schedule.SpeedLimitKBps > 0
            ? Settings.Schedule.SpeedLimitKBps
            : Settings.GlobalSpeedLimitKBps;
        _globalLimiter.BytesPerSecond = kbps * 1024;
    }

    /// <summary>Applies changed settings (speed limit, concurrency, rules, AI...) to running components.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
        ApplySpeedLimit();
        Queue.MaxConcurrent = settings.MaxConcurrentDownloads;
        ApplyQueues();
        SchedulerTick(DateTime.Now);
    }

    /// <summary>Adds several links at once (batch add from the clipboard).</summary>
    public IReadOnlyList<DownloadJob> AddMany(IEnumerable<(Uri Url, AddOptions Options)> items) =>
        items.Select(i => Add(i.Url, i.Options)).ToList();

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
        var job = new DownloadJob(request) { ForcedFolder = options.Folder };
        if (!string.IsNullOrWhiteSpace(options.QueueId)) job.QueueId = options.QueueId;
        if (options.Scheduled)
        {
            job.IsScheduled = true;
            if (!IsScheduleWindowOpen) job.Status = DownloadStatus.Scheduled;
        }
        else if (options.StartPaused)
        {
            job.Status = DownloadStatus.Paused;
        }
        return Queue.Enqueue(job);
    }

    /// <summary>Probes the URL and asks the advisor where it should go (used by the popup).</summary>
    /// <remarks>
    /// If the server cannot be reached or refuses the check (e.g. 503), the suggestion is still made from the
    /// link itself and <see cref="LastSuggestWarning"/> explains what happened; the download can still be tried.
    /// </remarks>
    public async Task<(ProbeResult Probe, SaveSuggestion Suggestion)> SuggestAsync(
        Uri url, string? pageTitle = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        ProbeResult probe;
        string? probeWarning = null;
        try
        {
            probe = url.Scheme == Uri.UriSchemeFtp
                ? await new FtpDownloader { Timeout = TimeSpan.FromSeconds(15) }.ProbeAsync(url, ct).ConfigureAwait(false)
                : await HttpProbe.ProbeAsync(_http, url, headers, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException
                                       && url.Scheme == Uri.UriSchemeFtp && ex is not FtpException { Code: 550 })
        {
            probe = HttpProbe.FromUrlOnly(url);
            probeWarning = ex.Message;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not System.Net.HttpStatusCode.NotFound)
        {
            probe = HttpProbe.FromUrlOnly(url);
            probeWarning = ex.Message;
        }
        var suggestion = await Advisor.SuggestAsync(
            new SuggestRequest(probe.FileName ?? "download", url, probe.ContentType, probe.Length, pageTitle), ct)
            .ConfigureAwait(false);
        LastSuggestWarning = string.Join(" · ", new[] { probeWarning, Advisor.LastWarning }.Where(w => !string.IsNullOrEmpty(w)));
        return (probe, suggestion);
    }

    /// <summary>Problems met by the last <see cref="SuggestAsync"/> (server check, AI, TMDB, media server).</summary>
    public string LastSuggestWarning { get; private set; } = "";

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
        var limiters = _queueLimiters.TryGetValue(Queue.EffectiveQueue(job), out var ql)
            ? new[] { _globalLimiter, ql }
            : new[] { _globalLimiter };
        DownloadResult result = job.Request.Url.Scheme == Uri.UriSchemeFtp
            ? await new FtpDownloader(limiters) { Timeout = job.Request.IdleTimeout }
                .DownloadAsync(job.Request, progress, ct).ConfigureAwait(false)
            : await new SegmentedDownloader(_http, limiters)
                .DownloadAsync(job.Request, progress, ct).ConfigureAwait(false);
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

        if (Settings.AutoExtract && job.FilePath is { } file && ArchiveExtractor.CanExtract(file))
            await ExtractCoreAsync(job, Settings.DeleteArchiveAfterExtract, ct).ConfigureAwait(false);
    }

    /// <summary>"Extract here" from the card menu. Returns false (and sets <see cref="DownloadJob.ExtractError"/>) on failure.</summary>
    public async Task<bool> ExtractAsync(DownloadJob job, CancellationToken ct = default)
    {
        if (job.FilePath is not { } file || !File.Exists(file) || !ArchiveExtractor.CanExtract(file)) return false;
        await ExtractCoreAsync(job, false, ct).ConfigureAwait(false);
        job.Raise();
        return job.ExtractError is null;
    }

    private static async Task ExtractCoreAsync(DownloadJob job, bool deleteArchive, CancellationToken ct)
    {
        try
        {
            job.ExtractError = null;
            job.ExtractedTo = await ArchiveExtractor.ExtractAsync(job.FilePath!, deleteArchive, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // The download itself is fine; only the extraction failed (damaged, password, no 7-Zip …).
            job.ExtractError = ex.Message;
        }
    }

    /// <summary>Main queue plus the user queues from the settings (names, limits, speed caps).</summary>
    public IReadOnlyList<QueueDefinition> AllQueues =>
        new[] { new QueueDefinition { Id = DownloadQueue.MainQueueId, Name = "", MaxConcurrent = Settings.MaxConcurrentDownloads } }
            .Concat(Settings.Queues).ToList();

    private void ApplyQueues()
    {
        var limiters = new Dictionary<string, RateLimiter>();
        foreach (var q in Settings.Queues)
        {
            // Keep the existing limiter object: running downloads hold a reference to it.
            var limiter = _queueLimiters.TryGetValue(q.Id, out var old) ? old : new RateLimiter();
            limiter.BytesPerSecond = Math.Max(0, q.SpeedLimitKBps) * 1024;
            limiters[q.Id] = limiter;
        }
        _queueLimiters = limiters;
        Queue.SetQueues(Settings.Queues.Select(q => (q.Id, q.MaxConcurrent)));
    }

    public void Dispose()
    {
        _historyTimer?.Dispose();
        SaveHistory();
        _schedulerTimer.Dispose();
        _http.Dispose();
        _apiHttp.Dispose();
    }
}
