using System.Net;
using System.Net.Sockets;

namespace SDM.Core.Tests;

/// <summary>Minimal local HTTP server that serves one byte array, with optional Range support.</summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _requests;

    public byte[] Content { get; }
    public bool SupportRanges { get; init; } = true;

    /// <summary>When set, the server closes each response after this many bytes (simulates a flaky link).</summary>
    public int? DropAfterBytes { get; set; }

    /// <summary>Server-side throttle (bytes per write + delay) so a test can cancel mid-download.</summary>
    public int ChunkDelayMs { get; set; }

    public string FileName { get; init; } = "file.bin";

    /// <summary>When set, requests beyond this many at once get 503 (like servers that limit connections per IP).</summary>
    public int? MaxConcurrent { get; set; }

    private int _active;
    public int PeakConcurrent;
    public int Rejected;
    public string ETag { get; set; } = "\"v1\"";
    private readonly int _port;
    public Uri Url => new($"http://127.0.0.1:{_port}/files/{Uri.EscapeDataString(FileName)}");
    public int RequestCount => Volatile.Read(ref _requests);

    public TestHttpServer(byte[] content, bool supportRanges = true)
    {
        Content = content;
        SupportRanges = supportRanges;
        _port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public static byte[] RandomBytes(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref _requests);
        var res = ctx.Response;
        int now = Interlocked.Increment(ref _active);
        try
        {
            if (MaxConcurrent is int max && now > max && ctx.Request.Headers["Range"] != "bytes=0-0")
            {
                Interlocked.Increment(ref Rejected);
                res.StatusCode = 503;
                res.Close();
                return;
            }
            int peak;
            while (now > (peak = Volatile.Read(ref PeakConcurrent)) && Interlocked.CompareExchange(ref PeakConcurrent, now, peak) != peak) { }

            long start = 0, end = Content.Length - 1;
            string? range = ctx.Request.Headers["Range"];
            res.Headers["ETag"] = ETag;
            res.ContentType = "application/octet-stream";

            if (SupportRanges && range is not null && range.StartsWith("bytes="))
            {
                var parts = range["bytes=".Length..].Split('-');
                start = long.Parse(parts[0]);
                if (parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), Content.Length - 1);
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {start}-{end}/{Content.Length}";
                res.Headers["Accept-Ranges"] = "bytes";
            }
            else res.StatusCode = 200;

            long count = end - start + 1;
            res.ContentLength64 = count;
            int limit = DropAfterBytes ?? int.MaxValue;
            int sent = 0;
            const int chunk = 16 * 1024;
            while (sent < count)
            {
                int n = (int)Math.Min(chunk, count - sent);
                if (sent + n > limit) n = limit - sent;
                if (n <= 0) { res.Abort(); return; }
                await res.OutputStream.WriteAsync(Content.AsMemory((int)(start + sent), n));
                sent += n;
                if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
            }
            res.Close();
        }
        catch
        {
            try { res.Abort(); } catch { }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
