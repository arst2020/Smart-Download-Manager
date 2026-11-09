using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SDM.Core.Browser;

/// <summary>
/// Local named pipe between short-lived processes (native host, a second app instance) and the
/// running app. One JSON line in, one JSON line out, per connection. The pipe name includes the
/// user name so two Windows users never see each other's app.
/// </summary>
public static class AppPipe
{
    public static string DefaultName => "SmartDownloadManager-" + Environment.UserName;

    /// <summary>Sends a message to the running app. Throws TimeoutException if no app is listening.</summary>
    public static async Task<BrowserReply> SendAsync(BrowserMessage message, string? pipeName = null,
        TimeSpan? connectTimeout = null, CancellationToken ct = default)
    {
        await using var client = new NamedPipeClientStream(".", pipeName ?? DefaultName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)(connectTimeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds, ct).ConfigureAwait(false);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, BrowserMessage.Json).AsMemory(), ct).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        return line is null
            ? BrowserReply.Failure("No answer from the app")
            : JsonSerializer.Deserialize<BrowserReply>(line, BrowserMessage.Json) ?? BrowserReply.Failure("Bad answer");
    }

    /// <summary>
    /// Sends the message, starting the app first if it is not running (used by the native host).
    /// </summary>
    public static async Task<BrowserReply> SendOrLaunchAsync(BrowserMessage message, Func<bool> launchApp,
        string? pipeName = null, TimeSpan? startupWait = null, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync(message, pipeName, TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!launchApp()) return BrowserReply.Failure("Could not start Smart Download Manager");
        }

        var deadline = DateTime.UtcNow + (startupWait ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await SendAsync(message, pipeName, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        return BrowserReply.Failure("Smart Download Manager did not start in time");
    }
}

/// <summary>Listens on the app's pipe and hands each message to <see cref="MessageReceived"/>.</summary>
public sealed class AppPipeServer : IDisposable
{
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public AppPipeServer(string? pipeName = null) => _name = pipeName ?? AppPipe.DefaultName;

    /// <summary>Raised on a background thread. Return the reply quickly; do slow UI work asynchronously.</summary>
    public Func<BrowserMessage, BrowserReply>? MessageReceived { get; set; }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500).ConfigureAwait(false); continue; }

            _ = Task.Run(() => HandleAsync(server));
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                BrowserReply reply;
                try
                {
                    var message = line is null ? null : JsonSerializer.Deserialize<BrowserMessage>(line, BrowserMessage.Json);
                    reply = message is null
                        ? BrowserReply.Failure("Empty message")
                        : MessageReceived?.Invoke(message) ?? BrowserReply.Failure("Not ready");
                }
                catch (JsonException ex)
                {
                    reply = BrowserReply.Failure(ex.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, BrowserMessage.Json).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // client went away; nothing to do
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
