using System.Text.Json;
using SDM.Core.Browser;

namespace SDM.Core.Tests;

public class NativeMessagingTests
{
    [Fact]
    public async Task Frames_round_trip_with_utf8()
    {
        using var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, """{"type":"download","pageTitle":"سریال جیران"}""");
        ms.Position = 0;
        Assert.Equal(4 + System.Text.Encoding.UTF8.GetByteCount("""{"type":"download","pageTitle":"سریال جیران"}"""), (int)ms.Length);
        Assert.Equal("""{"type":"download","pageTitle":"سریال جیران"}""", await NativeMessaging.ReadAsync(ms));
        Assert.Null(await NativeMessaging.ReadAsync(ms)); // end of stream = browser closed
    }

    [Fact]
    public async Task Host_loop_forwards_each_message_and_answers()
    {
        var input = new MemoryStream();
        await NativeMessaging.WriteAsync(input, """{"type":"download","url":"https://a.com/x.zip","cookies":"s=1"}""");
        await NativeMessaging.WriteAsync(input, "not json");
        input.Position = 0;
        var output = new MemoryStream();
        var seen = new List<BrowserMessage>();

        await NativeMessaging.RunHostAsync(input, output, (m, _) => { seen.Add(m); return Task.FromResult(BrowserReply.Success()); });

        output.Position = 0;
        var first = JsonSerializer.Deserialize<BrowserReply>((await NativeMessaging.ReadAsync(output))!)!;
        var second = JsonSerializer.Deserialize<BrowserReply>((await NativeMessaging.ReadAsync(output))!)!;
        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Single(seen);
        Assert.Equal("s=1", seen[0].ToHeaders()["Cookie"]);
    }

    [Fact]
    public void Message_headers_and_links()
    {
        var m = new BrowserMessage
        {
            Url = "https://a.com/1.mkv",
            Links = new() { "https://a.com/1.mkv", "javascript:void(0)", "https://a.com/2.mkv", "blob:https://x" },
            Referrer = "https://a.com/page", UserAgent = "Mozilla/5.0 Test", Cookies = ""
        };
        var h = m.ToHeaders();
        Assert.Equal("https://a.com/page", h["Referer"]);
        Assert.Equal("Mozilla/5.0 Test", h["User-Agent"]);
        Assert.False(h.ContainsKey("Cookie"));
        Assert.Equal(2, m.ValidLinks().Count);
    }

    [Fact]
    public void Host_manifests_and_launch_detection()
    {
        string chrome = NativeHostManifest.ForChromium(@"C:\Apps\SDM\SmartDownloadManager.exe");
        Assert.True(chrome.Contains("chrome-extension://hlmploemhlgmkommeccngdhdpjggjcdi/") && chrome.Contains("\"type\": \"stdio\""));
        string firefox = NativeHostManifest.ForFirefox("/x");
        Assert.True(firefox.Contains("sdm@smartdownloadmanager.app"));

        Assert.True(NativeHostManifest.IsHostLaunch(new[] { "chrome-extension://abc/", "--parent-window=0" }));
        Assert.True(NativeHostManifest.IsHostLaunch(new[] { @"C:\x\host.json", "sdm@smartdownloadmanager.app" }));
        Assert.False(NativeHostManifest.IsHostLaunch(new[] { "https://a.com/file.zip" }));
    }
}

public class AppPipeTests
{
    private static string UniqueName() => "sdm-test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Server_receives_message_and_replies()
    {
        string name = UniqueName();
        BrowserMessage? got = null;
        using var server = new AppPipeServer(name) { MessageReceived = m => { got = m; return BrowserReply.Success(); } };
        server.Start();

        var reply = await AppPipe.SendAsync(new BrowserMessage { Type = "batch", Links = new() { "https://a.com/1" } }, name, TimeSpan.FromSeconds(5));

        Assert.True(reply.Ok);
        Assert.Equal("batch", got!.Type);
    }

    [Fact]
    public async Task No_app_listening_times_out()
    {
        await Assert.ThrowsAnyAsync<TimeoutException>(() =>
            AppPipe.SendAsync(new BrowserMessage(), UniqueName(), TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Send_or_launch_starts_the_app_then_delivers()
    {
        string name = UniqueName();
        AppPipeServer? server = null;
        int launches = 0;
        try
        {
            var reply = await AppPipe.SendOrLaunchAsync(new BrowserMessage { Url = "https://a.com/x.zip" }, () =>
            {
                launches++;
                // Simulates the app starting a moment later.
                _ = Task.Delay(400).ContinueWith(_ =>
                {
                    server = new AppPipeServer(name) { MessageReceived = _ => BrowserReply.Success() };
                    server.Start();
                });
                return true;
            }, name, TimeSpan.FromSeconds(10));

            Assert.True(reply.Ok, reply.Error);
            Assert.Equal(1, launches);
        }
        finally
        {
            server?.Dispose();
        }
    }

    [Fact]
    public async Task Native_host_end_to_end_through_pipe()
    {
        // Browser → (framed stdin) → host loop → pipe → app → reply → (framed stdout) → browser
        string name = UniqueName();
        BrowserMessage? delivered = null;
        using var server = new AppPipeServer(name) { MessageReceived = m => { delivered = m; return BrowserReply.Success(); } };
        server.Start();

        var stdin = new MemoryStream();
        await NativeMessaging.WriteAsync(stdin, """{"type":"download","url":"https://cdn.example.com/Dark.S01E01.mkv","referrer":"https://example.com/dark"}""");
        stdin.Position = 0;
        var stdout = new MemoryStream();

        await NativeMessaging.RunHostAsync(stdin, stdout, (m, ct) => AppPipe.SendOrLaunchAsync(m, () => false, name, ct: ct));

        stdout.Position = 0;
        var reply = JsonSerializer.Deserialize<BrowserReply>((await NativeMessaging.ReadAsync(stdout))!)!;
        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("https://example.com/dark", delivered!.Referrer);
    }
}
