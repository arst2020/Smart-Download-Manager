using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SDM.Core.Tests;

/// <summary>Minimal FTP server for tests: USER/PASS, TYPE, SIZE, MDTM, EPSV/PASV, REST, RETR, QUIT.</summary>
public sealed class FakeFtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, byte[]> _files = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool SupportEpsv { get; set; } = true;
    public bool SupportRest { get; set; } = true;
    /// <summary>Stop sending after this many bytes of one RETR (simulates a dropped connection), once.</summary>
    public long? DropAfterBytes { get; set; }
    public int ChunkDelayMs { get; set; }
    public long LastRestOffset { get; private set; }
    public string? LastUser { get; private set; }
    public string? LastPassword { get; private set; }

    public FakeFtpServer()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    public void AddFile(string path, byte[] data) => _files[path] = data;

    public Uri UrlFor(string path, string? userInfo = null) =>
        new($"ftp://{(userInfo is null ? "" : userInfo + "@")}127.0.0.1:{Port}{path}");

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Handle(client);
            }
        }
        catch { /* stopped */ }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        TcpListener? passive = null;
        long rest = 0;
        try
        {
            await writer.WriteLineAsync("220-Fake FTP\r\n220 ready");
            while (await reader.ReadLineAsync() is { } line)
            {
                string cmd = line.Split(' ', 2)[0].ToUpperInvariant();
                string arg = line.Length > cmd.Length + 1 ? line[(cmd.Length + 1)..] : "";
                switch (cmd)
                {
                    case "USER": LastUser = arg; await writer.WriteLineAsync("331 password please"); break;
                    case "PASS": LastPassword = arg; await writer.WriteLineAsync("230 logged in"); break;
                    case "OPTS": await writer.WriteLineAsync("200 ok"); break;
                    case "TYPE": await writer.WriteLineAsync("200 binary"); break;
                    case "SIZE":
                        if (_files.TryGetValue(arg, out var f)) await writer.WriteLineAsync($"213 {f.Length}");
                        else await writer.WriteLineAsync("550 not found");
                        break;
                    case "MDTM": await writer.WriteLineAsync("213 20240102030405"); break;
                    case "EPSV":
                        if (!SupportEpsv) { await writer.WriteLineAsync("500 unknown"); break; }
                        passive = new TcpListener(IPAddress.Loopback, 0); passive.Start();
                        await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive.LocalEndpoint).Port}|)");
                        break;
                    case "PASV":
                        passive = new TcpListener(IPAddress.Loopback, 0); passive.Start();
                        int p = ((IPEndPoint)passive.LocalEndpoint).Port;
                        await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{p / 256},{p % 256})");
                        break;
                    case "REST":
                        if (!SupportRest) { await writer.WriteLineAsync("502 no"); break; }
                        rest = long.Parse(arg); LastRestOffset = rest;
                        await writer.WriteLineAsync($"350 restarting at {rest}");
                        break;
                    case "RETR":
                        if (!_files.TryGetValue(arg, out var data) || passive is null) { await writer.WriteLineAsync("550 not found"); break; }
                        await writer.WriteLineAsync("150 opening");
                        using (var dc = await passive.AcceptTcpClientAsync())
                        {
                            var ds = dc.GetStream();
                            long pos = rest, sent = 0;
                            bool drop = false;
                            while (pos < data.Length)
                            {
                                int n = (int)Math.Min(16 * 1024, data.Length - pos);
                                if (DropAfterBytes is long d && sent + n > d) { n = (int)(d - sent); drop = true; }
                                if (n > 0) await ds.WriteAsync(data.AsMemory((int)pos, n));
                                pos += n; sent += n;
                                if (drop) break;
                                if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
                            }
                            passive.Stop(); passive = null; rest = 0;
                            if (drop)
                            {
                                DropAfterBytes = null;
                                await writer.WriteLineAsync("426 connection closed; transfer aborted");
                                client.Close();
                                return;
                            }
                        }
                        await writer.WriteLineAsync("226 done");
                        break;
                    case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                    default: await writer.WriteLineAsync("502 not implemented"); break;
                }
            }
        }
        catch { /* client went away */ }
        finally { passive?.Stop(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
