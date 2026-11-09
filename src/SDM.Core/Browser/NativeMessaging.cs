using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SDM.Core.Browser;

/// <summary>
/// Chrome/Edge/Firefox native messaging framing: each message is UTF-8 JSON preceded by its
/// length as a 32-bit integer in native byte order (little-endian on Windows and x64).
/// </summary>
public static class NativeMessaging
{
    /// <summary>Browsers cap host→browser messages at 1 MB; we cap incoming ones at 4 MB to be safe.</summary>
    public const int MaxIncoming = 4 * 1024 * 1024;

    public static async Task<string?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(input, header, ct).ConfigureAwait(false)) return null; // browser closed the pipe
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxIncoming) throw new InvalidDataException($"Bad message length {length}.");

        var body = new byte[length];
        if (!await ReadExactlyAsync(input, body, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteAsync(Stream output, string json, CancellationToken ct = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The host loop the browser starts: read each message, hand it to <paramref name="forward"/>
    /// (which delivers it to the app), and answer with the result.
    /// </summary>
    public static async Task RunHostAsync(Stream input, Stream output,
        Func<BrowserMessage, CancellationToken, Task<BrowserReply>> forward, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? json = await ReadAsync(input, ct).ConfigureAwait(false);
            if (json is null) return;

            BrowserReply reply;
            try
            {
                var message = JsonSerializer.Deserialize<BrowserMessage>(json, BrowserMessage.Json)
                              ?? throw new InvalidDataException("Empty message");
                reply = await forward(message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or TimeoutException)
            {
                reply = BrowserReply.Failure(ex.Message);
            }
            await WriteAsync(output, JsonSerializer.Serialize(reply, BrowserMessage.Json), ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }
}
