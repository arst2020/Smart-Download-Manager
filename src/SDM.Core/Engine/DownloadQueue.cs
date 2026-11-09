using SDM.Core.Classification;
using SDM.Core.Models;

namespace SDM.Core.Engine;

/// <summary>A download in the queue; raises <see cref="Changed"/> when its status or progress changes.</summary>
public sealed class DownloadJob
{
    private CancellationTokenSource? _cts;

    public Guid Id { get; } = Guid.NewGuid();
    public DownloadRequest Request { get; }
    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;
    public DownloadProgress Progress { get; internal set; }
    public string? FileName { get; internal set; }
    public string? FilePath { get; internal set; }
    public ClassificationResult? Classification { get; internal set; }
    public MoveRecord? Move { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset Added { get; } = DateTimeOffset.Now;

    /// <summary>Overrides automatic organizing for this job (e.g. the user picked a folder).</summary>
    public string? ForcedFolder { get; init; }

    /// <summary>Belongs to the scheduler: runs only inside the schedule's time window.</summary>
    public bool IsScheduled { get; internal set; }

    public DateTimeOffset? Started { get; internal set; }
    public DateTimeOffset? Finished { get; internal set; }

    public event Action<DownloadJob>? Changed;

    public DownloadJob(DownloadRequest request) => Request = request;

    internal CancellationToken Begin()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    internal void RequestStop() => _cts?.Cancel();

    internal void Raise() => Changed?.Invoke(this);
}

/// <summary>Runs at most <see cref="MaxConcurrent"/> jobs at once; pause = cancel with state saved.</summary>
public sealed class DownloadQueue
{
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

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_lock) return _jobs.ToList(); }
    }

    public event Action<DownloadJob>? JobAdded;
    public event Action<DownloadJob>? JobRemoved;

    /// <summary>Raised once when a job reaches Completed (used for the "download finished" popup).</summary>
    public event Action<DownloadJob>? JobCompleted;

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
            foreach (var job in _jobs)
            {
                if (_running.Count + toStart.Count >= _maxConcurrent) break;
                if (job.Status == DownloadStatus.Queued && !_running.Contains(job)) toStart.Add(job);
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
