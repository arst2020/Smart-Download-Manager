using System.Diagnostics;

namespace SDM.Core.Engine;

/// <summary>
/// Token-bucket bandwidth limiter. One instance can be shared by every segment
/// (global limit) and another used per download; a segment waits on all of them.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private double _tokens;
    private long _lastTimestamp;
    private long _bytesPerSecond;

    public RateLimiter(long bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>0 = unlimited. Can be changed while downloads are running.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                long bps = BytesPerSecond;
                long now = Stopwatch.GetTimestamp();
                if (bps <= 0)
                {
                    _tokens = 0;
                    _lastTimestamp = now;
                    return;
                }

                double elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
                _lastTimestamp = now;

                // Allow at most one second of burst.
                double capacity = Math.Max(bps, bytes);
                _tokens = Math.Min(capacity, _tokens + elapsed * bps);

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                delay = TimeSpan.FromSeconds((bytes - _tokens) / bps);
            }

            if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
