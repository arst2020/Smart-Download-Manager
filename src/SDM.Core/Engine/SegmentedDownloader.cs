using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using SDM.Core.Models;
using SDM.Core.Util;

namespace SDM.Core.Engine;

/// <summary>
/// Multi-connection HTTP downloader. Each segment is fetched with its own Range request
/// and written straight to its offset in a single ".sdmpart" file, so no merge step is needed.
/// Progress per segment is saved to ".sdmstate" every two seconds, which makes resume possible
/// after a crash, a pause (cancellation) or a dropped connection.
///
/// The end of a download stays fast:
///   - a connection that finishes its part takes over half of the biggest part still left (work stealing),
///     so the last megabytes are not left to one slow connection;
///   - a connection that slows to a trickle (servers often throttle single connections) is dropped and
///     reopened from where it stopped.
/// </summary>
public sealed class SegmentedDownloader
{
    private const int BufferSize = 64 * 1024;

    /// <summary>A part is only split when at least twice this much is left of it.</summary>
    internal const long MinSplitSize = 512 * 1024;


    /// <summary>This many slow windows in a row and the connection is reopened.</summary>
    internal const int SlowWindowsBeforeReconnect = 3;

    /// <summary>Slow = below this fraction of the best speed one connection reached in this download.</summary>
    internal const double SlowFraction = 0.05;

    /// <summary>Slow-connection detection starts once one connection has reached this speed (bytes/s).</summary>
    internal const double SlowDetectionMinPeak = 100 * 1024;

    private const int MaxSlowReconnects = 30;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http;
    private readonly IReadOnlyList<RateLimiter> _limiters;

    /// <summary>Each connection's speed is checked over windows of this length (slow-connection detection).</summary>
    public TimeSpan SlowCheckWindow { get; init; } = TimeSpan.FromSeconds(5);

    public SegmentedDownloader(HttpClient http, IEnumerable<RateLimiter>? limiters = null)
    {
        _http = http;
        _limiters = limiters?.ToArray() ?? Array.Empty<RateLimiter>();
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(request.Directory);

        ProbeResult probe = await ProbeWithRetryAsync(request, ct).ConfigureAwait(false);
        string fileName = FileNameHelper.Sanitize(request.FileName ?? probe.FileName ?? "download");
        string finalPath = Path.Combine(request.Directory, fileName);
        string partPath = finalPath + DownloadStateStore.PartExtension;
        string statePath = finalPath + DownloadStateStore.StateExtension;

        DownloadState? state = DownloadStateStore.TryLoad(statePath);
        bool resumed = state is not null && File.Exists(partPath) && IsResumable(state, probe);
        if (!resumed)
        {
            state = CreateState(request, probe, fileName);
            if (File.Exists(partPath)) File.Delete(partPath);
        }

        using (var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (state!.TotalSize is long total && fs.Length != total)
                fs.SetLength(total);
        }
        DownloadStateStore.Save(statePath, state);

        using var handle = File.OpenHandle(partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
            FileOptions.Asynchronous);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        int active = 0;
        var gate = new ConnectionGate(request.MaxSegments);
        void ActiveDelta(int d) => Interlocked.Add(ref active, d);
        var run = new RunContext(state);

        // One worker per connection. Each starts with an unfinished part (or, when resuming with fewer parts
        // left than connections, by splitting one) and keeps taking over halves of the biggest part left.
        var pending = new Queue<Segment>(state.Segments.Where(s => !s.IsComplete));
        int workers = state.SupportsRange ? Math.Max(pending.Count, Math.Max(1, request.MaxSegments)) : pending.Count;
        var segmentTasks = Enumerable.Range(0, workers)
            .Select(_ => pending.Count > 0 ? pending.Dequeue() : null)
            .Select(async first =>
            {
                try
                {
                    var segment = first ?? (state.SupportsRange ? run.TrySplit() : null);
                    while (segment is not null)
                    {
                        await RunSegmentAsync(request, probe, run, segment, handle, gate, ActiveDelta, linked.Token).ConfigureAwait(false);
                        segment = state.SupportsRange ? run.TrySplit() : null;
                    }
                }
                catch { linked.Cancel(); throw; } // one segment failing for good stops the others
            })
            .ToList();

        using var monitorStop = new CancellationTokenSource();
        Task monitor = MonitorAsync(run, statePath, progress, () => Volatile.Read(ref active), monitorStop.Token);

        try
        {
            await Task.WhenAll(segmentTasks).ConfigureAwait(false);
        }
        catch
        {
            monitorStop.Cancel();
            await monitor.ConfigureAwait(false);
            run.Save(statePath);
            ct.ThrowIfCancellationRequested();
            // Surface the first real error rather than the cancellations it caused.
            var real = segmentTasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (real is not null) throw real;
            throw;
        }

        monitorStop.Cancel();
        await monitor.ConfigureAwait(false);
        handle.Dispose();

        long size = new FileInfo(partPath).Length;
        if (state.TotalSize is long expected && size != expected)
            throw new IOException($"Size mismatch: expected {expected} bytes, got {size}.");

        string target = FileNameHelper.MakeUnique(finalPath);
        File.Move(partPath, target);
        File.Delete(statePath);

        progress?.Report(new DownloadProgress(size, size, 0, 0)
        {
            SegmentFractions = state.Segments.Select(_ => 1.0).ToArray()
        });
        return new DownloadResult(target, size, probe, resumed);
    }

    /// <summary>A busy server (503/429) or a dropped connection gets a few more tries before giving up.</summary>
    private async Task<ProbeResult> ProbeWithRetryAsync(DownloadRequest request, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await HttpProbe.ProbeAsync(_http, request.Url, request.Headers, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested && attempt <= request.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsResumable(DownloadState state, ProbeResult probe)
    {
        if (!state.SupportsRange || !probe.AcceptRanges) return false;
        if (state.TotalSize != probe.Length) return false;
        if (state.ETag is not null && probe.ETag is not null && state.ETag != probe.ETag) return false;
        if (state.LastModified is not null && probe.LastModified is not null && state.LastModified != probe.LastModified)
            return false;
        return state.Segments.Count > 0;
    }

    internal static DownloadState CreateState(DownloadRequest request, ProbeResult probe, string fileName)
    {
        var state = new DownloadState
        {
            Url = request.Url.ToString(),
            FileName = fileName,
            TotalSize = probe.Length,
            SupportsRange = probe.AcceptRanges && probe.Length is > 0,
            ETag = probe.ETag,
            LastModified = probe.LastModified,
        };
        state.Segments.AddRange(Split(state.SupportsRange ? probe.Length : null, request.MaxSegments, request.MinSegmentSize));
        return state;
    }

    /// <summary>Splits [0, total) into up to <paramref name="maxSegments"/> ranges of at least <paramref name="minSize"/>.</summary>
    internal static IEnumerable<Segment> Split(long? total, int maxSegments, long minSize)
    {
        if (total is not long t || t <= 0)
        {
            yield return new Segment { Index = 0, Start = 0, End = -1 };
            yield break;
        }

        int count = (int)Math.Clamp(t / Math.Max(1, minSize), 1, Math.Max(1, maxSegments));
        long size = t / count;
        for (int i = 0; i < count; i++)
        {
            long start = i * size;
            long end = i == count - 1 ? t - 1 : start + size - 1;
            yield return new Segment { Index = i, Start = start, End = end };
        }
    }

    private async Task RunSegmentAsync(DownloadRequest request, ProbeResult probe, RunContext run,
        Segment segment, SafeFileHandle handle, ConnectionGate gate, Action<int> activeDelta, CancellationToken ct)
    {
        int attempt = 0, slowReconnects = 0;
        while (!segment.IsComplete)
        {
            Exception? failure = null;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            activeDelta(+1);
            try
            {
                await DownloadSegmentOnceAsync(request, probe, run, segment, handle, ct).ConfigureAwait(false);
                attempt = 0;
            }
            catch (SlowConnectionException) when (!ct.IsCancellationRequested && ++slowReconnects <= MaxSlowReconnects)
            {
                // Trickling connection: open a fresh one right away from the same position (no back-off).
                continue;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                failure = ex;
            }
            finally
            {
                activeDelta(-1);
                gate.Release();
            }

            if (failure is null) continue;
            if (failure is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable })
                gate.Reduce(); // the server limits connections per client: use fewer
            if (++attempt > request.MaxRetries) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            double seconds = Math.Min(30, 0.5 * Math.Pow(2, attempt - 1));
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code == HttpStatusCode.TooManyRequests
                                                         || code == HttpStatusCode.RequestTimeout,
        HttpRequestException => true, // connection-level failure
        IOException => true,
        OperationCanceledException => true, // idle timeout (caller token is checked separately)
        _ => false
    };

    private async Task DownloadSegmentOnceAsync(DownloadRequest request, ProbeResult probe, RunContext run,
        Segment segment, SafeFileHandle handle, CancellationToken ct)
    {
        var state = run.State;
        bool ranged = state.SupportsRange;
        using var message = new HttpRequestMessage(HttpMethod.Get, probe.FinalUri);
        HttpHeaderHelper.Apply(message, request.Headers);
        if (ranged)
        {
            message.Headers.Range = new RangeHeaderValue(segment.Start + segment.Downloaded, segment.End);
            if (state.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
                message.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (ranged && response.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidOperationException("The server stopped honouring range requests (file changed?).");

        if (!ranged) segment.Downloaded = 0; // no resume possible: restart from byte 0

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var window = Stopwatch.StartNew();
        var connection = Stopwatch.StartNew();
        long windowBytes = 0, connectionBytes = 0;
        int slowWindows = 0;
        try
        {
            while (true)
            {
                // End can move: another connection may have taken over the second half of this part.
                long remaining = ranged ? segment.Length - segment.Downloaded : long.MaxValue;
                if (remaining <= 0) break;
                int toRead = (int)Math.Min(buffer.Length, remaining);

                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(request.IdleTimeout);
                    read = await stream.ReadAsync(buffer.AsMemory(0, toRead), idle.Token).ConfigureAwait(false);
                }
                if (read == 0) break;

                foreach (var limiter in _limiters)
                    await limiter.WaitAsync(read, ct).ConfigureAwait(false);

                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), segment.Start + segment.Downloaded, ct)
                    .ConfigureAwait(false);
                segment.Advance(read);

                windowBytes += read;
                connectionBytes += read;
                if (window.Elapsed >= SlowCheckWindow)
                {
                    double rate = windowBytes / window.Elapsed.TotalSeconds;
                    double peak = run.ReportRate(rate);
                    bool slow = ranged && peak >= SlowDetectionMinPeak && rate < peak * SlowFraction
                                && segment.Length - segment.Downloaded > 64 * 1024; // even the last bytes: a trickle is the tail problem
                    slowWindows = slow ? slowWindows + 1 : 0;
                    if (slowWindows >= SlowWindowsBeforeReconnect) throw new SlowConnectionException(rate);
                    windowBytes = 0;
                    window.Restart();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // A fast connection may finish before a full window: still count its speed for the comparison.
        if (connectionBytes >= MinSplitSize && connection.Elapsed > TimeSpan.Zero)
            run.ReportRate(connectionBytes / Math.Max(connection.Elapsed.TotalSeconds, 0.01));

        if (ranged)
        {
            if (!segment.IsComplete)
                throw new IOException("Connection closed before the segment was complete.");
        }
        else
        {
            if (state.TotalSize is long total && segment.Downloaded != total)
                throw new IOException("Connection closed before the file was complete.");
            segment.Done = true;
        }
    }

    private static async Task MonitorAsync(RunContext run, string statePath,
        IProgress<DownloadProgress>? progress, Func<int> active, CancellationToken stop)
    {
        var state = run.State;
        var sw = Stopwatch.StartNew();
        TimeSpan lastSave = TimeSpan.Zero, lastSample = TimeSpan.Zero;
        long lastBytes = run.DownloadedBytes();
        double speed = 0;

        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(ProgressInterval, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            long bytes = run.DownloadedBytes();
            TimeSpan now = sw.Elapsed;
            double dt = (now - lastSample).TotalSeconds;
            if (dt > 0)
            {
                double instant = (bytes - lastBytes) / dt;
                speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3; // smoothed
            }
            lastBytes = bytes;
            lastSample = now;
            progress?.Report(new DownloadProgress(bytes, state.TotalSize, speed, active())
            {
                SegmentFractions = run.Snapshot()
                    .Select(s => s.IsComplete ? 1.0 : s.Length > 0 ? Math.Min(1.0, (double)s.Downloaded / s.Length) : 0.0)
                    .ToArray()
            });

            if (now - lastSave >= SaveInterval)
            {
                try { run.Save(statePath); } catch (IOException) { /* next tick */ }
                lastSave = now;
            }
        }
    }

    /// <summary>
    /// Shared state of one download run: the part list (which grows when parts are split), guarded by a lock,
    /// and the best per-connection speed seen (for spotting trickling connections).
    /// </summary>
    internal sealed class RunContext
    {
        private readonly object _lock = new();
        private double _peakRate;

        public RunContext(DownloadState state) => State = state;

        public DownloadState State { get; }

        public Segment[] Snapshot()
        {
            lock (_lock) return State.Segments.ToArray();
        }

        /// <summary>Bytes really downloaded (a part whose end moved may have read a little past it).</summary>
        public long DownloadedBytes() =>
            Snapshot().Sum(s => s.Length >= 0 ? Math.Min(s.Downloaded, s.Length) : s.Downloaded);

        public void Save(string statePath)
        {
            lock (_lock) DownloadStateStore.Save(statePath, State);
        }

        /// <summary>Records one connection's speed; returns the best seen so far.</summary>
        public double ReportRate(double rate)
        {
            lock (_lock)
            {
                if (rate > _peakRate) _peakRate = rate;
                return _peakRate;
            }
        }

        /// <summary>
        /// Work stealing: takes the second half of the part with the most bytes left and returns it as a new part
        /// (the original part now ends where the new one starts). Null when no part is big enough to split.
        /// </summary>
        public Segment? TrySplit()
        {
            lock (_lock)
            {
                Segment? victim = null;
                long best = 0;
                foreach (var seg in State.Segments)
                {
                    if (seg.IsComplete || seg.Length <= 0) continue;
                    long left = seg.Length - seg.Downloaded;
                    if (left > best) { best = left; victim = seg; }
                }
                if (victim is null || best < 2 * MinSplitSize) return null;

                long mid = victim.Start + victim.Downloaded + best / 2;
                var stolen = new Segment
                {
                    Index = State.Segments.Max(x => x.Index) + 1,
                    Start = mid,
                    End = victim.End,
                };
                victim.End = mid - 1;
                State.Segments.Add(stolen);
                return stolen;
            }
        }
    }
}

/// <summary>A connection that slowed to a trickle; it is reopened from where it stopped.</summary>
internal sealed class SlowConnectionException : IOException
{
    public SlowConnectionException(double rate) : base($"Connection too slow ({rate:0} B/s), reconnecting.") { }
}
