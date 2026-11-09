using System.Diagnostics;
using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Models;
using SDM.Core.Settings;

namespace SDM.Core.Tests;

public class DownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-dl-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = HttpClientFactory.Create(new AppSettings());

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DownloadRequest Request(Uri url, int segments = 4) => new()
    {
        Url = url,
        Directory = _dir,
        MaxSegments = segments,
        MinSegmentSize = 64 * 1024,
        MaxRetries = 3,
        IdleTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public void Split_covers_every_byte_once()
    {
        var segments = SegmentedDownloader.Split(10_000_003, 8, 1_000_000).ToList();
        Assert.Equal(8, segments.Count);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(10_000_002, segments[^1].End);
        for (int i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End + 1, segments[i].Start);
    }

    [Fact]
    public void Small_files_use_one_segment()
    {
        Assert.Single(SegmentedDownloader.Split(500, 8, 1024 * 1024));
    }

    [Fact]
    public async Task Downloads_with_multiple_segments()
    {
        var data = TestHttpServer.RandomBytes(1_500_000);
        using var server = new TestHttpServer(data);

        var result = await new SegmentedDownloader(_http).DownloadAsync(Request(server.Url));

        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal("file.bin", Path.GetFileName(result.FilePath));
        Assert.False(File.Exists(result.FilePath + DownloadStateStore.StateExtension));
        Assert.True(server.RequestCount >= 5); // probe + 4 segments
    }

    [Fact]
    public async Task Works_when_server_ignores_ranges()
    {
        var data = TestHttpServer.RandomBytes(300_000);
        using var server = new TestHttpServer(data, supportRanges: false);

        var result = await new SegmentedDownloader(_http).DownloadAsync(Request(server.Url));

        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
        Assert.False(result.Probe.AcceptRanges);
    }

    [Fact]
    public async Task Resumes_after_cancellation()
    {
        var data = TestHttpServer.RandomBytes(2_000_000);
        using var server = new TestHttpServer(data) { ChunkDelayMs = 100 };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SegmentedDownloader(_http).DownloadAsync(Request(server.Url), null, cts.Token));

        string part = Path.Combine(_dir, "file.bin" + DownloadStateStore.PartExtension);
        string statePath = Path.Combine(_dir, "file.bin" + DownloadStateStore.StateExtension);
        Assert.True(File.Exists(part));
        var state = DownloadStateStore.TryLoad(statePath);
        Assert.NotNull(state);
        long before = state!.Segments.Sum(s => s.Downloaded);
        Assert.InRange(before, 1, data.Length - 1);

        server.ChunkDelayMs = 0;
        var result = await new SegmentedDownloader(_http).DownloadAsync(Request(server.Url));

        Assert.True(result.Resumed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task Restarts_when_file_changed_on_server()
    {
        var data = TestHttpServer.RandomBytes(1_000_000);
        using var server = new TestHttpServer(data) { ChunkDelayMs = 100 };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SegmentedDownloader(_http).DownloadAsync(Request(server.Url), null, cts.Token));

        server.ETag = "\"v2\"";
        server.ChunkDelayMs = 0;
        var result = await new SegmentedDownloader(_http).DownloadAsync(Request(server.Url));

        Assert.False(result.Resumed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task Retries_dropped_connections()
    {
        var data = TestHttpServer.RandomBytes(400_000);
        using var server = new TestHttpServer(data) { DropAfterBytes = 100_000 };

        var result = await new SegmentedDownloader(_http).DownloadAsync(Request(server.Url, segments: 2));

        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task Uses_32_connections_and_backs_off_when_server_limits_them()
    {
        var data = TestHttpServer.RandomBytes(4_000_000);
        using var server = new TestHttpServer(data) { MaxConcurrent = 4, ChunkDelayMs = 5 };
        var request = new DownloadRequest
        {
            Url = server.Url, Directory = _dir, MaxSegments = 32, MinSegmentSize = 64 * 1024, MaxRetries = 8,
        };

        var result = await new SegmentedDownloader(_http).DownloadAsync(request);

        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
        Assert.True(server.Rejected > 0, "server should have rejected some of the 32 connections");
    }

    [Fact]
    public async Task Opens_up_to_32_connections_at_once()
    {
        var data = TestHttpServer.RandomBytes(4_000_000);
        using var server = new TestHttpServer(data) { ChunkDelayMs = 20 };
        var request = new DownloadRequest { Url = server.Url, Directory = _dir, MaxSegments = 32, MinSegmentSize = 64 * 1024 };

        await new SegmentedDownloader(_http).DownloadAsync(request);

        Assert.True(server.PeakConcurrent >= 24, $"peak was {server.PeakConcurrent}");
    }

    [Fact]
    public async Task Does_not_overwrite_existing_file()
    {
        var data = TestHttpServer.RandomBytes(10_000);
        using var server = new TestHttpServer(data);
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "file.bin"), "old");

        var result = await new SegmentedDownloader(_http).DownloadAsync(Request(server.Url));

        Assert.Equal("file (2).bin", Path.GetFileName(result.FilePath));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_dir, "file.bin")));
    }

    [Fact]
    public async Task Rate_limiter_slows_download()
    {
        var data = TestHttpServer.RandomBytes(300_000);
        using var server = new TestHttpServer(data);
        var limiter = new RateLimiter(200_000); // ~1.5 s for 300 KB

        var sw = Stopwatch.StartNew();
        await new SegmentedDownloader(_http, new[] { limiter }).DownloadAsync(Request(server.Url));
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.0), $"took {sw.Elapsed}");
    }
}

public class SmartDownloadServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-svc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Episode_is_downloaded_and_moved_to_season_folder()
    {
        var data = TestHttpServer.RandomBytes(200_000);
        using var server = new TestHttpServer(data) { FileName = "Dark.S01E03.1080p.mkv" };

        var settings = new AppSettings { DownloadDirectory = _dir, MaxSegments = 2 };
        using var service = new SmartDownloadService(settings);
        var job = service.Add(server.Url);
        await service.Queue.WhenIdleAsync();

        Assert.Equal(DownloadStatus.Completed, job.Status);
        // Renamed to the Emby/Jellyfin standard by default.
        string expected = Path.Combine(_dir, "Series", "Dark", "Season 01", "Dark - S01E03.mkv");
        Assert.Equal(expected, job.FilePath);
        Assert.Equal(data, await File.ReadAllBytesAsync(expected));

        Assert.True(service.Mover.Undo(job.Move!));
        Assert.True(File.Exists(Path.Combine(_dir, "Dark.S01E03.1080p.mkv"))); // original name restored
    }

    [Fact]
    public async Task Keep_original_name_when_rename_is_off()
    {
        var data = TestHttpServer.RandomBytes(50_000);
        using var server = new TestHttpServer(data) { FileName = "Dark.S01E04.mkv" };
        var settings = new AppSettings { DownloadDirectory = _dir, RenameToStandard = false };
        using var service = new SmartDownloadService(settings);
        var job = service.Add(server.Url, new AddOptions { Connections = 1 });
        await service.Queue.WhenIdleAsync();
        Assert.Equal(Path.Combine(_dir, "Series", "Dark", "Season 01", "Dark.S01E04.mkv"), job.FilePath);
        Assert.Equal(1, job.Request.MaxSegments);
    }

    [Fact]
    public async Task Pause_and_resume_through_queue()
    {
        var data = TestHttpServer.RandomBytes(1_000_000);
        using var server = new TestHttpServer(data) { ChunkDelayMs = 100, FileName = "big.zip" };

        var settings = new AppSettings { DownloadDirectory = _dir, MaxSegments = 2 };
        using var service = new SmartDownloadService(settings);
        var job = service.Add(server.Url);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (job.Progress.Downloaded == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        service.Queue.Pause(job);
        await service.Queue.WhenIdleAsync();
        Assert.Equal(DownloadStatus.Paused, job.Status);

        server.ChunkDelayMs = 0;
        service.Queue.Resume(job);
        await Task.Delay(50);
        await service.Queue.WhenIdleAsync();

        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Equal(Path.Combine(_dir, "Compressed", "big.zip"), job.FilePath);
        Assert.Equal(data, await File.ReadAllBytesAsync(job.FilePath!));
    }
}
