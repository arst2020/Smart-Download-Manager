using SDM.Core.Classification;
using SDM.Core.Models;

namespace SDM.Core.Engine;

/// <summary>A download in the queue; raises <see cref="Changed"/> when its status or progress changes.</summary>
public sealed class DownloadJob
{
    private CancellationTokenSource? _cts;

    public Guid Id { get; private init; } = Guid.NewGuid();
    public DownloadRequest Request { get; }
    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;
    public DownloadProgress Progress { get; internal set; }
    public string? FileName { get; internal set; }
    public string? FilePath { get; internal set; }
    public ClassificationResult? Classification { get; internal set; }
    public MoveRecord? Move { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset Added { get; private init; } = DateTimeOffset.Now;

    /// <summary>Overrides automatic organizing for this job (e.g. the user picked a folder).</summary>
    public string? ForcedFolder { get; init; }

    /// <summary>Which queue this job waits in (<see cref="DownloadQueue.MainQueueId"/> or a user queue's id).</summary>
    public string QueueId { get; internal set; } = DownloadQueue.MainQueueId;

    /// <summary>Belongs to the scheduler: runs only inside the schedule's time window.</summary>
    public bool IsScheduled { get; internal set; }

    /// <summary>Folder the archive was extracted to (auto-extract or "Extract here").</summary>
    public string? ExtractedTo { get; internal set; }

    /// <summary>Why extraction failed (the download itself is still complete).</summary>
    public string? ExtractError { get; internal set; }

    public DateTimeOffset? Started { get; internal set; }
    public DateTimeOffset? Finished { get; internal set; }

    public event Action<DownloadJob>? Changed;

    public DownloadJob(DownloadRequest request) => Request = request;

    /// <summary>Snapshot for the history file.</summary>
    public DownloadRecord ToRecord() => new()
    {
        Id = Id,
        Url = Request.Url.ToString(),
        Directory = Request.Directory,
        RequestedFileName = Request.FileName,
        Connections = Request.MaxSegments,
        Headers = Request.Headers.Count == 0 ? null : new Dictionary<string, string>(Request.Headers),
        PageTitle = Request.PageTitle,
        ForcedFolder = ForcedFolder,
        IsScheduled = IsScheduled,
        QueueId = QueueId == DownloadQueue.MainQueueId ? null : QueueId,
        Status = Status,
        FileName = FileName,
        FilePath = FilePath,
        Downloaded = Progress.Downloaded,
        Total = Progress.Total,
        Category = Classification?.Category,
        Folder = Classification?.Folder,
        Source = Classification?.Source,
        Explanation = Classification?.Explanation,
        MovedFrom = Move?.From,
        MovedTo = Move?.To,
        Error = Error,
        ExtractedTo = ExtractedTo,
        Added = Added,
        Started = Started,
        Finished = Finished,
    };

    /// <summary>Rebuilds a job from the history file (it is not running).</summary>
    public static DownloadJob FromRecord(DownloadRecord r, int maxRetries = 5)
    {
        var request = new DownloadRequest
        {
            Url = new Uri(r.Url),
            Directory = r.Directory,
            FileName = r.RequestedFileName,
            MaxSegments = Math.Clamp(r.Connections, 1, 32),
            MaxRetries = maxRetries,
            Headers = r.Headers ?? new Dictionary<string, string>(),
            PageTitle = r.PageTitle,
        };
        return new DownloadJob(request)
        {
            Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
            Added = r.Added,
            ForcedFolder = r.ForcedFolder,
            IsScheduled = r.IsScheduled,
            QueueId = string.IsNullOrEmpty(r.QueueId) ? DownloadQueue.MainQueueId : r.QueueId,
            Status = r.Status,
            FileName = r.FileName,
            FilePath = r.FilePath,
            Progress = new DownloadProgress(r.Downloaded, r.Total, 0, 0),
            Classification = r.Folder is null ? null
                : new ClassificationResult(r.Category ?? "", r.Folder, 1, r.Source ?? "", null, r.Explanation),
            Move = r.MovedFrom is not null && r.MovedTo is not null
                ? new MoveRecord(r.MovedFrom, r.MovedTo, r.Finished ?? r.Added, r.Category ?? "")
                : null,
            Error = r.Error,
            ExtractedTo = r.ExtractedTo,
            Started = r.Started,
            Finished = r.Finished,
        };
    }

    internal CancellationToken Begin()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    internal void RequestStop() => _cts?.Cancel();

    internal void Raise() => Changed?.Invoke(this);
}

/// <summary>
/// Runs jobs with a limit per queue: the main queue runs <see cref="MaxConcurrent"/> at once, each user queue
/// its own number. Pause = cancel with state saved.
/// </summary>
public sealed class DownloadQueue
{
    public const string MainQueueId = "main";
    private Dictionary<string, int> _queueLimits = new();
    private readonly object _lock = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly HashSet<DownloadJob> _running = new();
    private readonly HashSet<DownloadJob> _pausing = new();
    private readonly HashSet<DownloadJob> _parking = new(); // stopped by the scheduler → back to Scheduled
    private readonly Func<DownloadJob, IProgress<DownloadProgress>, CancellationToken, Task> _run;
    private int _maxConcurrent;

    /// <param name="run">Does the actual work for a job (download + organize).</param>
    public DownloadQueue(Func<DownloadJob, IProgress<DownloadProgress>, CancellationToken, Task> run, int maxConcurrent = 3)
    {
        _run = run;
        _maxConcurrent = Math.Max(1, maxConcurrent);
    }

    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set { _maxConcurrent = Math.Max(1, value); Pump(); }
    }

    /// <summary>Sets the user queues and their parallel limits. Jobs of a queue that no longer exists run in the main queue.</summary>
    public void SetQueues(IEnumerable<(string Id, int MaxConcurrent)> queues)
    {
        var limits = new Dictionary<string, int>();
        foreach (var (id, max) in queues)
            if (!string.IsNullOrWhiteSpace(id) && id != MainQueueId) limits[id] = Math.Max(1, max);
        lock (_lock) _queueLimits = limits;
        Pump();
    }

    /// <summary>The queue a job really runs in (its own, or main if that queue was deleted).</summary>
    public string EffectiveQueue(DownloadJob job)
    {
        lock (_lock) return _queueLimits.ContainsKey(job.QueueId) ? job.QueueId : MainQueueId;
    }

    private int LimitOf(string queueId) => _queueLimits.TryGetValue(queueId, out int n) ? n : _maxConcurrent;

    public void MoveToQueue(DownloadJob job, string queueId)
    {
        if (job.QueueId == queueId) return;
        job.QueueId = string.IsNullOrWhiteSpace(queueId) ? MainQueueId : queueId;
        job.Raise();
        Pump();
    }

    /// <summary>Starts every paused / failed / stopped download of a queue.</summary>
    public void StartQueue(string queueId)
    {
        foreach (var job in Jobs.Where(j => EffectiveQueue(j) == queueId)) Resume(job);
    }

    /// <summary>Pauses every running or waiting download of a queue.</summary>
    public void PauseQueue(string queueId)
    {
        foreach (var job in Jobs.Where(j => EffectiveQueue(j) == queueId)) Pause(job);
    }

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_lock) return _jobs.ToList(); }
    }

    public event Action<DownloadJob>? JobAdded;
    public event Action<DownloadJob>? JobRemoved;

    /// <summary>Raised once when a job reaches Completed (used for the "download finished" popup).</summary>
    public event Action<DownloadJob>? JobCompleted;

    /// <summary>Raised when a job ends as Failed (after its retries).</summary>
    public event Action<DownloadJob>? JobFailed;

    public DownloadJob Enqueue(DownloadJob job)
    {
        lock (_lock) _jobs.Add(job);
        JobAdded?.Invoke(job);
        Pump();
        return job;
    }

    public void Pause(DownloadJob job)
    {
        lock (_lock)
        {
            if (_running.Contains(job)) { _pausing.Add(job); job.RequestStop(); return; }
            if (job.Status != DownloadStatus.Queued) return;
            job.Status = DownloadStatus.Paused;
        }
        job.Raise();
    }

    public void Resume(DownloadJob job)
    {
        lock (_lock)
        {
            if (job.Status is not (DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled
                or DownloadStatus.Scheduled)) return;
            job.Status = DownloadStatus.Queued;
            job.Error = null;
        }
        job.Raise();
        Pump();
    }

    /// <summary>Removes completed downloads from the list (their files stay on disk).</summary>
    public int RemoveCompleted()
    {
        List<DownloadJob> done;
        lock (_lock)
        {
            done = _jobs.Where(j => j.Status == DownloadStatus.Completed).ToList();
            foreach (var j in done) _jobs.Remove(j);
        }
        foreach (var j in done) JobRemoved?.Invoke(j);
        return done.Count;
    }

    /// <summary>Moves jobs to the scheduler (they wait for the time window).</summary>
    public void Schedule(IEnumerable<DownloadJob> jobs)
    {
        var changed = new List<DownloadJob>();
        lock (_lock)
        {
            foreach (var job in jobs)
            {
                job.IsScheduled = true;
                if (_running.Contains(job)) { _parking.Add(job); job.RequestStop(); continue; }
                if (job.Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Canceled)
                {
                    job.Status = DownloadStatus.Scheduled;
                    changed.Add(job);
                }
            }
        }
        foreach (var j in changed) j.Raise();
    }

    /// <summary>The window opened: scheduled jobs join the queue.</summary>
    public void ReleaseScheduled()
    {
        var changed = new List<DownloadJob>();
        lock (_lock)
        {
            foreach (var job in _jobs.Where(j => j.Status == DownloadStatus.Scheduled))
            {
                job.Status = DownloadStatus.Queued;
                changed.Add(job);
            }
        }
        foreach (var j in changed) j.Raise();
        Pump();
    }

    /// <summary>The window closed: running and queued scheduled jobs go back to waiting (progress is kept).</summary>
    public void ParkScheduled()
    {
        var changed = new List<DownloadJob>();
        lock (_lock)
        {
            foreach (var job in _jobs.Where(j => j.IsScheduled))
            {
                if (_running.Contains(job)) { _parking.Add(job); job.RequestStop(); }
                else if (job.Status == DownloadStatus.Queued) { job.Status = DownloadStatus.Scheduled; changed.Add(job); }
            }
        }
        foreach (var j in changed) j.Raise();
    }

    /// <summary>Stops and removes a job. Partial files stay on disk, so the same URL can resume later.</summary>
    public void Remove(DownloadJob job)
    {
        lock (_lock)
        {
            if (_running.Contains(job)) job.RequestStop();
            _jobs.Remove(job);
        }
        JobRemoved?.Invoke(job);
    }

    public Task WhenIdleAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        while (true)
        {
            lock (_lock)
                if (_running.Count == 0 && !_jobs.Any(j => j.Status == DownloadStatus.Queued)) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }, ct);

    private void Pump()
    {
        List<DownloadJob> toStart = new();
        lock (_lock)
        {
            var busy = new Dictionary<string, int>();
            string QueueOf(DownloadJob j) => _queueLimits.ContainsKey(j.QueueId) ? j.QueueId : MainQueueId;
            foreach (var job in _running)
                busy[QueueOf(job)] = busy.GetValueOrDefault(QueueOf(job)) + 1;
            foreach (var job in _jobs)
            {
                if (job.Status != DownloadStatus.Queued || _running.Contains(job)) continue;
                string q = QueueOf(job);
                int n = busy.GetValueOrDefault(q);
                if (n >= LimitOf(q)) continue;
                busy[q] = n + 1;
                toStart.Add(job);
            }
            foreach (var job in toStart)
            {
                _running.Add(job);
                job.Status = DownloadStatus.Probing;
            }
        }
        foreach (var job in toStart) _ = RunJobAsync(job);
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        job.Started ??= DateTimeOffset.Now;
        job.Raise();
        CancellationToken ct = job.Begin();
        try
        {
            await Task.Run(() => _run(job, new SyncProgress(job), ct), ct).ConfigureAwait(false);
            job.Status = DownloadStatus.Completed;
            job.Finished = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            bool paused, parked;
            lock (_lock) { paused = _pausing.Remove(job); parked = _parking.Remove(job); }
            job.Status = parked ? DownloadStatus.Scheduled : paused ? DownloadStatus.Paused : DownloadStatus.Canceled;
        }
        catch (Exception ex)
        {
            job.Status = DownloadStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            lock (_lock) { _running.Remove(job); _pausing.Remove(job); _parking.Remove(job); }
        }
        job.Raise();
        if (job.Status == DownloadStatus.Completed) JobCompleted?.Invoke(job);
        else if (job.Status == DownloadStatus.Failed) JobFailed?.Invoke(job);
        Pump();
    }

    /// <summary>Reports progress on the calling thread; UI layers marshal to their own dispatcher.</summary>
    private sealed class SyncProgress(DownloadJob job) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            job.Progress = value;
            if (job.Status == DownloadStatus.Probing) job.Status = DownloadStatus.Downloading;
            job.Raise();
        }
    }
}
