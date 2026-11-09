using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SDM.Core.Models;
using SDM.Core.Util;

namespace SDM.Core.Engine;

/// <summary>
/// Downloads ftp:// links over one connection (passive mode, binary), resuming with REST.
/// The partial file and a small .sdmstate (size + modification time) sit next to the target like HTTP downloads.
/// User and password come from the link (ftp://user:pass@host/file); otherwise anonymous.
/// </summary>
public sealed class FtpDownloader
{
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<RateLimiter> _limiters;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public FtpDownloader(IEnumerable<RateLimiter>? limiters = null) =>
        _limiters = limiters?.ToArray() ?? Array.Empty<RateLimiter>();

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request.Url.Scheme != Uri.UriSchemeFtp) throw new ArgumentException("Not an ftp:// link", nameof(request));
        Directory.CreateDirectory(request.Directory);

        int attempt = 0;
        while (true)
        {
            try
            {
                return await DownloadOnceAsync(request, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < request.MaxRetries && IsTransient(ex))
            {
                attempt++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Asks the server for size and date (for the Add window) without downloading.</summary>
    public async Task<ProbeResult> ProbeAsync(Uri url, CancellationToken ct = default)
    {
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        using var control = new FtpControl(Timeout);
        await control.ConnectAsync(url.Host, url.Port > 0 ? url.Port : 21, ct).ConfigureAwait(false);
        var (user, pass) = Credentials(url);
        await control.LoginAsync(user, pass, ct).ConfigureAwait(false);
        await control.CommandAsync("TYPE I", ct, 200).ConfigureAwait(false);
        long? size = await control.TrySizeAsync(path, ct).ConfigureAwait(false);
        DateTimeOffset? modified = await control.TryModifiedAsync(path, ct).ConfigureAwait(false);
        try { await control.CommandAsync("QUIT", ct).ConfigureAwait(false); } catch { /* closing anyway */ }
        string name = Path.GetFileName(path);
        return new ProbeResult(url, size, true, string.IsNullOrEmpty(name) ? null : name, null, null, modified);
    }

    private static bool IsTransient(Exception ex) =>
        ex is IOException or SocketException or TimeoutException || ex is FtpException { Transient: true };

    private async Task<DownloadResult> DownloadOnceAsync(DownloadRequest request, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        Uri url = request.Url;
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        string urlName = Path.GetFileName(path);
        string fileName = FileNameHelper.Sanitize(request.FileName ?? (string.IsNullOrEmpty(urlName) ? "download" : urlName));
        string finalPath = Path.Combine(request.Directory, fileName);
        string partPath = finalPath + DownloadStateStore.PartExtension;
        string statePath = finalPath + DownloadStateStore.StateExtension;

        using var control = new FtpControl(Timeout);
        await control.ConnectAsync(url.Host, url.Port > 0 ? url.Port : 21, ct).ConfigureAwait(false);
        var (user, pass) = Credentials(url);
        await control.LoginAsync(user, pass, ct).ConfigureAwait(false);
        await control.CommandAsync("TYPE I", ct, 200).ConfigureAwait(false);

        long? size = await control.TrySizeAsync(path, ct).ConfigureAwait(false);
        DateTimeOffset? modified = await control.TryModifiedAsync(path, ct).ConfigureAwait(false);

        // Resume only if the file on the server is the same one we started.
        DownloadState? state = DownloadStateStore.TryLoad(statePath);
        long offset = 0;
        if (state is not null && File.Exists(partPath) && state.TotalSize == size && state.LastModified == modified && size is not null)
            offset = new FileInfo(partPath).Length;
        else
        {
            state = new DownloadState { Url = url.AbsoluteUri, FileName = fileName, TotalSize = size, LastModified = modified, SupportsRange = true };
            if (File.Exists(partPath)) File.Delete(partPath);
        }
        if (size is long total && offset > total) offset = 0;
        DownloadStateStore.Save(statePath, state);

        var probe = new ProbeResult(url, size, true, fileName, null, null, modified);
        bool resumed = offset > 0;

        if (size is long s && offset == s && s > 0)
        {
            // Already complete (the app was closed right at the end).
            return Finish(partPath, statePath, finalPath, s, probe, resumed);
        }

        using var data = await control.OpenPassiveAsync(ct).ConfigureAwait(false);
        if (offset > 0)
        {
            var rest = await control.CommandAsync($"REST {offset}", ct).ConfigureAwait(false);
            if (rest.Code != 350) { offset = 0; resumed = false; } // server can't resume: start over
        }
        var retr = await control.CommandAsync($"RETR {path}", ct).ConfigureAwait(false);
        if (retr.Code is not (125 or 150)) throw new FtpException(retr);

        long downloaded = offset;
        using (var fs = new FileStream(partPath, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            fs.SetLength(offset);
            fs.Position = offset;
            var buffer = new byte[BufferSize];
            var stopwatch = Stopwatch.StartNew();
            var lastProgress = TimeSpan.Zero;
            var lastSave = TimeSpan.Zero;
            long speedBase = downloaded;
            var speedTime = TimeSpan.Zero;
            double speed = 0;
            using var stream = data.GetStream();

            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(Timeout);
                int read;
                try { read = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("FTP data connection stalled"); }
                if (read == 0) break;

                foreach (var limiter in _limiters) await limiter.WaitAsync(read, ct).ConfigureAwait(false);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;

                var now = stopwatch.Elapsed;
                if (now - speedTime >= TimeSpan.FromSeconds(1))
                {
                    speed = (downloaded - speedBase) / (now - speedTime).TotalSeconds;
                    speedBase = downloaded;
                    speedTime = now;
                }
                if (now - lastProgress >= ProgressInterval)
                {
                    lastProgress = now;
                    progress?.Report(new DownloadProgress(downloaded, size, speed, 1) { SegmentFractions = Fractions(downloaded, size) });
                }
                if (now - lastSave >= SaveInterval)
                {
                    lastSave = now;
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        data.Close();

        var done = await control.ReadReplyAsync(ct).ConfigureAwait(false);
        if (done.Code is not (226 or 250)) throw new FtpException(done);
        if (size is long expected && downloaded != expected)
            throw new IOException($"FTP transfer ended early ({downloaded} of {expected} bytes)");

        progress?.Report(new DownloadProgress(downloaded, size ?? downloaded, 0, 0) { SegmentFractions = new[] { 1.0 } });
        try { await control.CommandAsync("QUIT", ct).ConfigureAwait(false); } catch { /* closing anyway */ }
        return Finish(partPath, statePath, finalPath, downloaded, probe, resumed);
    }

    private static IReadOnlyList<double>? Fractions(long downloaded, long? size) =>
        size is > 0 ? new[] { Math.Min(1.0, downloaded / (double)size.Value) } : null;

    private static DownloadResult Finish(string partPath, string statePath, string finalPath, long size, ProbeResult probe, bool resumed)
    {
        string target = File.Exists(finalPath) ? FileNameHelper.MakeUnique(finalPath) : finalPath;
        File.Move(partPath, target);
        File.Delete(statePath);
        return new DownloadResult(target, size, probe, resumed);
    }

    private static (string User, string Pass) Credentials(Uri url)
    {
        if (string.IsNullOrEmpty(url.UserInfo)) return ("anonymous", "sdm@example.com");
        string[] parts = url.UserInfo.Split(':', 2);
        return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }
}

public sealed class FtpException : IOException
{
    public int Code { get; }

    /// <summary>4xx replies are temporary ("try again"); 5xx are permanent (no such file, login refused …).</summary>
    public bool Transient => Code is >= 400 and < 500;

    public FtpException(FtpReply reply) : base($"FTP {reply.Code}: {reply.Text}") => Code = reply.Code;
}

public readonly record struct FtpReply(int Code, string Text);

/// <summary>The FTP control connection: commands, multi-line replies, passive data connections.</summary>
internal sealed class FtpControl : IDisposable
{
    private readonly TimeSpan _timeout;
    private TcpClient? _client;
    private StreamReader? _reader;
    private Stream? _stream;
    private string _host = "";

    public FtpControl(TimeSpan timeout) => _timeout = timeout;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        _host = host;
        _client = new TcpClient();
        using (var cts = Linked(ct)) await _client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        _stream = _client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var hello = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (hello.Code != 220) throw new FtpException(hello);
    }

    public async Task LoginAsync(string user, string pass, CancellationToken ct)
    {
        var r = await CommandAsync($"USER {user}", ct).ConfigureAwait(false);
        if (r.Code == 331) r = await CommandAsync($"PASS {pass}", ct).ConfigureAwait(false);
        if (r.Code != 230) throw new FtpException(r);
        await CommandAsync("OPTS UTF8 ON", ct).ConfigureAwait(false); // ignored by servers that don't know it
    }

    public async Task<long?> TrySizeAsync(string path, CancellationToken ct)
    {
        var r = await CommandAsync($"SIZE {path}", ct).ConfigureAwait(false);
        if (r.Code == 213 && long.TryParse(r.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) return size;
        if (r.Code == 550) throw new FtpException(r); // no such file
        return null;
    }

    public async Task<DateTimeOffset?> TryModifiedAsync(string path, CancellationToken ct)
    {
        var r = await CommandAsync($"MDTM {path}", ct).ConfigureAwait(false);
        if (r.Code == 213 && DateTime.TryParseExact(r.Text.Trim()[..Math.Min(14, r.Text.Trim().Length)], "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return new DateTimeOffset(t, TimeSpan.Zero);
        return null;
    }

    /// <summary>EPSV first (works through NAT and IPv6), then PASV.</summary>
    public async Task<TcpClient> OpenPassiveAsync(CancellationToken ct)
    {
        string host = _host;
        int port;
        var r = await CommandAsync("EPSV", ct).ConfigureAwait(false);
        if (r.Code == 229)
        {
            // "Entering Extended Passive Mode (|||6446|)"
            int open = r.Text.IndexOf('('), close = r.Text.IndexOf(')');
            string[] fields = r.Text[(open + 1)..close].Split(r.Text[open + 1]);
            port = int.Parse(fields[3], CultureInfo.InvariantCulture);
        }
        else
        {
            r = await CommandAsync("PASV", ct).ConfigureAwait(false);
            if (r.Code != 227) throw new FtpException(r);
            // "Entering Passive Mode (h1,h2,h3,h4,p1,p2)"
            int open = r.Text.IndexOf('('), close = r.Text.IndexOf(')');
            int[] n = r.Text[(open + 1)..close].Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            port = n[4] * 256 + n[5];
            // Servers behind NAT often report a private address; the control host is the reliable one.
            var reported = IPAddress.Parse($"{n[0]}.{n[1]}.{n[2]}.{n[3]}");
            if (!IsPrivate(reported)) host = reported.ToString();
        }
        var data = new TcpClient();
        using (var cts = Linked(ct)) await data.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        return data;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 0;
    }

    public async Task<FtpReply> CommandAsync(string command, CancellationToken ct, int? expect = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command + "\r\n");
        using (var cts = Linked(ct)) await _stream!.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (expect is int e && reply.Code != e) throw new FtpException(reply);
        return reply;
    }

    /// <summary>Reads one reply, including "123-first line … 123 last line" multi-line replies.</summary>
    public async Task<FtpReply> ReadReplyAsync(CancellationToken ct)
    {
        using var cts = Linked(ct);
        string? line = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false)
                       ?? throw new IOException("FTP server closed the connection");
        if (line.Length < 3 || !int.TryParse(line[..3], out int code)) throw new IOException($"Bad FTP reply: {line}");
        var text = new StringBuilder(line.Length > 4 ? line[4..] : "");
        if (line.Length > 3 && line[3] == '-')
        {
            string end = $"{code} ";
            while (true)
            {
                line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false) ?? throw new IOException("FTP server closed the connection");
                if (line.StartsWith(end, StringComparison.Ordinal) || line == code.ToString(CultureInfo.InvariantCulture)) break;
                text.Append('\n').Append(line);
            }
        }
        return new FtpReply(code, text.ToString());
    }

    private CancellationTokenSource Linked(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        return cts;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _client?.Dispose();
    }
}
