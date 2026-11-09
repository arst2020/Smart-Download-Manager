namespace SDM.Core.Engine;

/// <summary>
/// Limits how many segments of one download talk to the server at the same time.
/// When the server answers 429/503 (too many connections) the limit is lowered,
/// so a file split into 32 segments can still finish on a server that allows only a few.
/// </summary>
public sealed class ConnectionGate
{
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();
    private int _limit;
    private int _debt; // permits to swallow on release after a reduction

    public ConnectionGate(int limit)
    {
        _limit = Math.Max(1, limit);
        _semaphore = new SemaphoreSlim(_limit, int.MaxValue);
    }

    public int Limit
    {
        get { lock (_lock) return _limit; }
    }

    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    public void Release()
    {
        lock (_lock)
        {
            if (_debt > 0) { _debt--; return; }
        }
        _semaphore.Release();
    }

    /// <summary>Halves the limit (minimum 1). Returns the new limit.</summary>
    public int Reduce()
    {
        lock (_lock)
        {
            int target = Math.Max(1, _limit / 2);
            _debt += _limit - target;
            _limit = target;
            return _limit;
        }
    }

    public static readonly int[] Choices = { 1, 4, 8, 16, 32 };
}
