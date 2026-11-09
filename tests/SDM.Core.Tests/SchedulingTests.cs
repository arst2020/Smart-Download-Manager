using SDM.Core.Models;
using SDM.Core.Scheduling;
using SDM.Core.Settings;
using SDM.Core.Util;

namespace SDM.Core.Tests;

public class ScheduleWindowTests
{
    private static ScheduleSettings S(string start, string stop, params DayOfWeek[] days) =>
        new() { Enabled = true, Start = start, Stop = stop, Days = days.ToList() };

    // 2026-11-09 is a Monday.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    [Fact]
    public void Same_day_window()
    {
        var s = S("09:00", "17:00");
        Assert.True(ScheduleWindow.IsOpen(s, At(9, 9)));
        Assert.True(ScheduleWindow.IsOpen(s, At(9, 16, 59)));
        Assert.False(ScheduleWindow.IsOpen(s, At(9, 17)));
        Assert.False(ScheduleWindow.IsOpen(s, At(9, 8, 59)));
    }

    [Fact]
    public void Overnight_window_belongs_to_the_start_day()
    {
        var s = S("23:00", "07:00", DayOfWeek.Monday);
        Assert.True(ScheduleWindow.IsOpen(s, At(9, 23, 30)));   // Monday night
        Assert.True(ScheduleWindow.IsOpen(s, At(10, 6, 59)));   // Tuesday morning, still Monday's window
        Assert.False(ScheduleWindow.IsOpen(s, At(10, 23, 30))); // Tuesday night: not allowed
        Assert.False(ScheduleWindow.IsOpen(s, At(9, 6)));       // Monday morning = Sunday's window
    }

    [Fact]
    public void Disabled_never_opens_and_next_start_is_found()
    {
        var off = S("02:00", "08:00");
        off.Enabled = false;
        Assert.False(ScheduleWindow.IsOpen(off, At(9, 3)));
        Assert.Null(ScheduleWindow.NextStart(off, At(9, 3)));

        var s = S("02:00", "08:00", DayOfWeek.Wednesday);
        Assert.Equal(At(11, 2), ScheduleWindow.NextStart(s, At(9, 12)));
    }
}

public class LinkExtractorTests
{
    [Fact]
    public void Extracts_dedupes_and_trims()
    {
        var r = LinkExtractor.Extract("""
            https://a.com/1.mkv
            see (https://a.com/2.mkv), and https://a.com/1.mkv
            ftp://files.example.org/x.zip
            not a link: www.example.com
            "https://b.org/file.pdf".
            """);
        Assert.Equal(4, r.Links.Count);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal("https://a.com/2.mkv", r.Links[1].ToString());
        Assert.Equal("https://b.org/file.pdf", r.Links[3].ToString());
    }

    [Fact]
    public void Empty_text()
    {
        Assert.Empty(LinkExtractor.Extract("  ").Links);
    }
}

public class ServiceSchedulingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-sch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private SmartDownloadService Service(out AppSettings settings)
    {
        settings = new AppSettings
        {
            DownloadDirectory = _dir, MaxSegments = 2,
            Schedule = new ScheduleSettings { Enabled = true, Start = "02:00", Stop = "08:00", Days = new(), AfterQueue = AfterQueueAction.Sleep },
        };
        var service = new SmartDownloadService(settings);
        service.StopSchedulerTimer();
        return service;
    }

    [Fact]
    public async Task Scheduled_job_waits_then_runs_and_reports_the_after_action()
    {
        var data = TestHttpServer.RandomBytes(100_000);
        using var server = new TestHttpServer(data) { FileName = "night.zip" };
        using var service = Service(out _);
        AfterQueueAction? fired = null;
        service.ScheduledQueueFinished += a => fired = a;

        service.SchedulerTick(new DateTime(2026, 11, 9, 12, 0, 0)); // outside
        var job = service.Add(server.Url, new SDM.Core.AddOptions { Scheduled = true });
        await Task.Delay(300);
        Assert.Equal(DownloadStatus.Scheduled, job.Status);

        service.SchedulerTick(new DateTime(2026, 11, 10, 2, 30, 0)); // window opens
        await service.Queue.WhenIdleAsync();
        Assert.Equal(DownloadStatus.Completed, job.Status);

        service.SchedulerTick(new DateTime(2026, 11, 10, 2, 31, 0));
        Assert.Equal(AfterQueueAction.Sleep, fired);
    }

    [Fact]
    public async Task Window_close_parks_running_job_and_keeps_progress()
    {
        var data = TestHttpServer.RandomBytes(2_000_000);
        using var server = new TestHttpServer(data) { FileName = "big.bin", ChunkDelayMs = 100 };
        using var service = Service(out _);

        service.SchedulerTick(new DateTime(2026, 11, 10, 3, 0, 0)); // open
        var job = service.Add(server.Url, new SDM.Core.AddOptions { Scheduled = true });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (job.Progress.Downloaded == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);

        service.SchedulerTick(new DateTime(2026, 11, 10, 8, 0, 0)); // close
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (job.Status != DownloadStatus.Scheduled && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(DownloadStatus.Scheduled, job.Status);
        Assert.True(File.Exists(Path.Combine(_dir, "big.bin.sdmstate")));
        Assert.NotNull(job.Progress.SegmentFractions);
    }
}

public class ProbeFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Range_rejected_with_503_falls_back_to_plain_request()
    {
        var data = TestHttpServer.RandomBytes(50_000);
        using var server = new TestHttpServer(data) { Reject503WithRange = true, FileName = "Chicago.PD.S01E01.mkv" };
        using var http = HttpClientFactory.Create(new AppSettings());

        var probe = await SDM.Core.Engine.HttpProbe.ProbeAsync(http, server.Url, null, default);

        Assert.Equal(50_000L, probe.Length);
        Assert.False(probe.AcceptRanges);
        Assert.True(server.LastUserAgent!.StartsWith("Mozilla/5.0"), server.LastUserAgent);
    }

    [Fact]
    public async Task Suggestion_still_works_when_server_says_503()
    {
        using var server = new TestHttpServer(new byte[10]) { AlwaysStatus = 503, FileName = "Chicago.P.D.S01E01.720p.mkv" };
        using var service = new SmartDownloadService(new AppSettings { DownloadDirectory = _dir });

        var (probe, s) = await service.SuggestAsync(server.Url);

        Assert.Null(probe.Length);
        Assert.Equal(SDM.Core.Media.MediaKind.Series, s.Media.Kind);
        Assert.True(service.LastSuggestWarning.Contains("503"), service.LastSuggestWarning);
    }

    [Fact]
    public void Old_user_agent_is_upgraded_on_load()
    {
        string path = Path.Combine(Path.GetTempPath(), "sdm-ua-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"UserAgent":"SmartDownloadManager/0.1"}""");
            Assert.Equal(AppSettings.DefaultUserAgent, SettingsStore.Load(path).UserAgent);
        }
        finally { File.Delete(path); }
    }
}

public class HistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-hist-" + Guid.NewGuid().ToString("N"));
    private string HistoryFile => Path.Combine(_dir, "downloads.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Record_round_trip_keeps_everything()
    {
        var job = new SDM.Core.Engine.DownloadJob(new DownloadRequest
        {
            Url = new Uri("https://a.com/x.zip"), Directory = "D", FileName = "x.zip", MaxSegments = 16,
            Headers = new Dictionary<string, string> { ["Cookie"] = "s=1" }, PageTitle = "Page"
        }) { ForcedFolder = "F" };
        var back = SDM.Core.Engine.DownloadJob.FromRecord(job.ToRecord());
        Assert.Equal(job.Id, back.Id);
        Assert.Equal(16, back.Request.MaxSegments);
        Assert.Equal("s=1", back.Request.Headers["Cookie"]);
        Assert.Equal("F", back.ForcedFolder);
        Assert.Equal(job.Added, back.Added);
    }

    [Fact]
    public async Task Completed_download_is_saved_and_restored()
    {
        var data = TestHttpServer.RandomBytes(30_000);
        using var server = new TestHttpServer(data) { FileName = "doc.pdf" };
        var settings = new AppSettings { DownloadDirectory = _dir };
        using (var service = new SmartDownloadService(settings))
        {
            service.EnableHistory(HistoryFile);
            service.Add(server.Url);
            await service.Queue.WhenIdleAsync();
            service.Shutdown();
        }

        using var again = new SmartDownloadService(settings);
        again.EnableHistory(HistoryFile);
        var job = again.Queue.Jobs.Single();
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Equal(Path.Combine(_dir, "Documents", "doc.pdf"), job.FilePath);
        Assert.Equal(1, again.Queue.RemoveCompleted());
        again.SaveHistory();
        Assert.Empty(SDM.Core.Engine.DownloadHistory.Load(HistoryFile));
    }

    [Fact]
    public async Task Running_download_continues_after_restart()
    {
        var data = TestHttpServer.RandomBytes(1_500_000);
        using var server = new TestHttpServer(data) { FileName = "big.iso", ChunkDelayMs = 60 };
        var settings = new AppSettings { DownloadDirectory = _dir, MaxSegments = 4 };
        using (var service = new SmartDownloadService(settings))
        {
            service.EnableHistory(HistoryFile);
            var job = service.Add(server.Url);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (job.Progress.Downloaded == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            service.Shutdown();   // app closes mid-download
        }
        Assert.Equal(DownloadStatus.Downloading, SDM.Core.Engine.DownloadHistory.Load(HistoryFile).Single().Status);

        server.ChunkDelayMs = 0;
        using var again = new SmartDownloadService(settings);
        again.EnableHistory(HistoryFile);   // ResumeOnStart = true
        await Task.Delay(100);
        await again.Queue.WhenIdleAsync();
        var restored = again.Queue.Jobs.Single();
        Assert.Equal(DownloadStatus.Completed, restored.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(restored.FilePath!));
    }

    [Fact]
    public void Damaged_history_file_starts_empty_and_is_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HistoryFile, "{ not json");
        Assert.Empty(SDM.Core.Engine.DownloadHistory.Load(HistoryFile));
        Assert.True(File.Exists(HistoryFile + ".broken"));
    }
}

public class ClipboardAndSubtitleTests
{
    [Theory]
    [InlineData("https://cdn.example.com/files/setup.exe", true)]
    [InlineData("https://cdn.example.com/Show.S01E03.mkv?token=abc", true)]
    [InlineData("https://cdn.example.com/My%20Archive.zip", true)]
    [InlineData("ftp://ftp.example.com/pub/data.iso", true)]
    [InlineData("https://news.example.com/article/123", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://example.com/photo.jpg", false)]
    [InlineData("https://example.com/index.html", false)]
    public void Looks_downloadable(string url, bool expected) =>
        Assert.Equal(expected, SDM.Core.Util.LinkExtractor.LooksDownloadable(new Uri(url)));

    [Fact]
    public void Extract_downloadable_skips_page_links()
    {
        var links = SDM.Core.Util.LinkExtractor.ExtractDownloadable(
            "see https://site.com/page and get https://site.com/a.zip plus https://site.com/b.pdf");
        Assert.Equal(2, links.Count);
    }

    [Theory]
    [InlineData("Breaking.Bad.S01E03.720p.WEB.mkv", "Breaking Bad S01E03")]
    [InlineData("Dune.Part.Two.2024.1080p.WEB-DL.mkv", "Dune Part Two 2024")]
    [InlineData("holiday_clip.mp4", "holiday clip")]
    public void Subtitle_query(string file, string expected) =>
        Assert.Equal(expected, SDM.Core.Media.SubtitleSearch.BuildQuery(file));

    [Fact]
    public void Subtitle_url_is_encoded_and_only_http()
    {
        Assert.Equal("https://subdl.com/search/Dune%20Part%20Two%202024",
            SDM.Core.Media.SubtitleSearch.BuildUrl(null, "Dune Part Two 2024")!.AbsoluteUri);
        Assert.Equal("https://www.google.com/search?q=A%20B+subtitles",
            SDM.Core.Media.SubtitleSearch.BuildUrl("https://www.google.com/search?q={query}+subtitles", "A B")!.AbsoluteUri);
        Assert.Null(SDM.Core.Media.SubtitleSearch.BuildUrl("file:///C:/Windows/System32/calc.exe?{query}", "x"));
        Assert.Null(SDM.Core.Media.SubtitleSearch.BuildUrl("cmd.exe /c {query}", "x"));
    }

    [Fact]
    public async Task Failed_event_fires()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sdm-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestHttpServer(new byte[10]) { FileName = "x.zip", AlwaysStatus = 404 };
            using var service = new SmartDownloadService(new AppSettings { DownloadDirectory = dir, MaxRetries = 0 });
            SDM.Core.Engine.DownloadJob? failed = null;
            service.Queue.JobFailed += j => failed = j;
            service.Add(server.Url);
            await service.Queue.WhenIdleAsync();
            Assert.NotNull(failed);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class FtpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-ftp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private DownloadRequest Req(Uri url) => new() { Url = url, Directory = _dir, MaxRetries = 3 };

    [Fact]
    public async Task Downloads_a_file_anonymously()
    {
        using var server = new FakeFtpServer();
        var data = TestHttpServer.RandomBytes(200_000);
        server.AddFile("/pub/My File.iso", data);
        var result = await new SDM.Core.Engine.FtpDownloader().DownloadAsync(Req(server.UrlFor("/pub/My%20File.iso")));
        Assert.Equal(Path.Combine(_dir, "My File.iso"), result.FilePath);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal("anonymous", server.LastUser);
        Assert.False(File.Exists(result.FilePath + ".sdmstate"));
    }

    [Fact]
    public async Task Uses_credentials_and_pasv_fallback()
    {
        using var server = new FakeFtpServer { SupportEpsv = false };
        var data = TestHttpServer.RandomBytes(50_000);
        server.AddFile("/a.zip", data);
        var result = await new SDM.Core.Engine.FtpDownloader().DownloadAsync(Req(server.UrlFor("/a.zip", "bob:s%40cret")));
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal("bob", server.LastUser);
        Assert.Equal("s@cret", server.LastPassword);
    }

    [Fact]
    public async Task Dropped_connection_resumes_with_rest()
    {
        using var server = new FakeFtpServer { DropAfterBytes = 120_000 };
        var data = TestHttpServer.RandomBytes(300_000);
        server.AddFile("/big.bin", data);
        var result = await new SDM.Core.Engine.FtpDownloader().DownloadAsync(Req(server.UrlFor("/big.bin")));
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal(120_000, server.LastRestOffset);
        Assert.True(result.Resumed);
    }

    [Fact]
    public async Task Missing_file_fails_without_retry()
    {
        using var server = new FakeFtpServer();
        var ex = await Assert.ThrowsAsync<SDM.Core.Engine.FtpException>(() =>
            new SDM.Core.Engine.FtpDownloader().DownloadAsync(Req(server.UrlFor("/nope.zip"))));
        Assert.Equal(550, ex.Code);
    }

    [Fact]
    public async Task Service_downloads_and_organizes_ftp_links()
    {
        using var server = new FakeFtpServer();
        server.AddFile("/docs/manual.pdf", TestHttpServer.RandomBytes(10_000));
        using var service = new SmartDownloadService(new AppSettings { DownloadDirectory = _dir });
        var job = service.Add(server.UrlFor("/docs/manual.pdf"));
        await service.Queue.WhenIdleAsync();
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Equal(Path.Combine(_dir, "Documents", "manual.pdf"), job.FilePath);
        var (probe, _) = await service.SuggestAsync(server.UrlFor("/docs/manual.pdf"));
        Assert.Equal(10_000, probe.Length);
    }
}

public class QueueTests
{
    private static SDM.Core.Engine.DownloadJob NewJob(string name) =>
        new(new DownloadRequest { Url = new Uri($"https://x.test/{name}"), Directory = Path.GetTempPath() });

    [Fact]
    public async Task Each_queue_has_its_own_limit()
    {
        var gate = new TaskCompletionSource();
        int running = 0, maxMain = 0, maxNight = 0;
        object l = new();
        SDM.Core.Engine.DownloadQueue? queue = null;
        queue = new SDM.Core.Engine.DownloadQueue(async (job, _, ct) =>
        {
            lock (l)
            {
                running++;
                int main = queue!.Jobs.Count(j => j.Status == DownloadStatus.Probing && queue.EffectiveQueue(j) == "main");
                int night = queue.Jobs.Count(j => j.Status == DownloadStatus.Probing && queue.EffectiveQueue(j) == "night");
                maxMain = Math.Max(maxMain, main); maxNight = Math.Max(maxNight, night);
            }
            await gate.Task.WaitAsync(ct);
        }, maxConcurrent: 2);
        queue.SetQueues(new[] { ("night", 1) });

        var jobs = Enumerable.Range(0, 4).Select(i => NewJob("m" + i)).ToList();
        var nightJobs = Enumerable.Range(0, 3).Select(i => NewJob("n" + i)).ToList();
        foreach (var j in nightJobs) queue.MoveToQueue(j, "night");
        foreach (var j in jobs.Concat(nightJobs)) queue.Enqueue(j);
        await Task.Delay(200);
        Assert.Equal(2, queue.Jobs.Count(j => j.Status == DownloadStatus.Probing && queue.EffectiveQueue(j) == "main"));
        Assert.Equal(1, queue.Jobs.Count(j => j.Status == DownloadStatus.Probing && queue.EffectiveQueue(j) == "night"));
        gate.SetResult();
        await queue.WhenIdleAsync();
        Assert.True(queue.Jobs.All(j => j.Status == DownloadStatus.Completed));
        Assert.Equal(2, maxMain);
        Assert.Equal(1, maxNight);
    }

    [Fact]
    public void Deleted_queue_falls_back_to_main_and_pause_queue_works()
    {
        var queue = new SDM.Core.Engine.DownloadQueue((_, _, ct) => Task.Delay(-1, ct), maxConcurrent: 1);
        queue.SetQueues(new[] { ("q1", 1) });
        var a = NewJob("a");
        queue.MoveToQueue(a, "q1");
        Assert.Equal("q1", queue.EffectiveQueue(a));
        queue.SetQueues(Array.Empty<(string, int)>());
        Assert.Equal("main", queue.EffectiveQueue(a));

        var b = NewJob("b"); var c = NewJob("c");
        queue.Enqueue(b); queue.Enqueue(c);
        queue.PauseQueue("main");
        Assert.Equal(DownloadStatus.Paused, c.Status);
    }

    [Fact]
    public void Queue_id_survives_history()
    {
        var job = NewJob("z");
        var queue = new SDM.Core.Engine.DownloadQueue((_, _, _) => Task.CompletedTask);
        queue.MoveToQueue(job, "night");
        Assert.Equal("night", SDM.Core.Engine.DownloadJob.FromRecord(job.ToRecord()).QueueId);
        Assert.Equal("main", SDM.Core.Engine.DownloadJob.FromRecord(NewJob("y").ToRecord()).QueueId);
    }
}

public class SpeedHistoryTests
{
    [Fact]
    public void Keeps_the_last_samples_in_order()
    {
        var h = new SpeedHistory(3);
        foreach (var v in new double[] { 1, 2, 3, 4 }) h.Add(v);
        Assert.Equal("2,3,4", string.Join(",", h.Samples));
        Assert.Equal(4.0, h.Peak);
        Assert.Equal(3.0, h.Average);
    }

    [Fact]
    public void Path_ends_at_the_right_edge_and_scales_to_peak()
    {
        var h = new SpeedHistory(5);
        h.Add(0); h.Add(100);
        Assert.Equal("M75,100 L100,8", h.ToPath(100, 100));
        Assert.Equal("M75,100 L75,100 L100,8 L100,100 Z", h.ToPath(100, 100, closed: true));
        Assert.Equal("", new SpeedHistory().ToPath(10, 10));
    }
}

public class HashAndExtractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdm-hx-" + Guid.NewGuid().ToString("N"));

    public HashAndExtractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Hashes_match_known_values()
    {
        string file = Path.Combine(_dir, "abc.txt");
        await File.WriteAllTextAsync(file, "abc");
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", await HashVerifier.ComputeAsync(file, HashAlgorithmKind.Md5));
        string sha = await HashVerifier.ComputeAsync(file, HashAlgorithmKind.Sha256);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sha);
        Assert.True(HashVerifier.Matches(sha, "SHA256: BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.True(HashVerifier.Matches(sha, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  abc.txt"));
        Assert.False(HashVerifier.Matches(sha, "deadbeef"));
        Assert.Equal(HashAlgorithmKind.Sha1, HashVerifier.Detect("a9993e364706816aba3e25717850c26c9cd0d89d"));
        Assert.Null(HashVerifier.Detect("xyz"));
    }

    [Theory]
    [InlineData("movie.part1.rar", false)]
    [InlineData("movie.part2.rar", true)]
    [InlineData("movie.part02.rar", true)]
    [InlineData("movie.r00", true)]
    [InlineData("data.7z.001", false)]
    [InlineData("data.7z.002", true)]
    [InlineData("tool.zip", false)]
    public void Later_parts_are_skipped(string name, bool later) =>
        Assert.Equal(later, ArchiveExtractor.IsLaterPart(name));

    [Fact]
    public async Task Zip_is_extracted_next_to_it()
    {
        string zip = Path.Combine(_dir, "Tool.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry("bin/readme.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("hello");
        }
        string dest = await ArchiveExtractor.ExtractAsync(zip);
        Assert.Equal(Path.Combine(_dir, "Tool"), dest);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dest, "bin", "readme.txt")));
        Assert.Equal(Path.Combine(_dir, "Tool (2)"), ArchiveExtractor.DestinationFor(zip));
    }

    [Fact]
    public async Task Zip_slip_is_refused()
    {
        string zip = Path.Combine(_dir, "evil.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("../../outside.txt").Open());
            w.Write("x");
        }
        await Assert.ThrowsAnyAsync<IOException>(() => ArchiveExtractor.ExtractAsync(zip));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "outside.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "evil")));
    }

    [Fact]
    public async Task Auto_extract_after_download()
    {
        var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(archive.CreateEntry("a.txt").Open());
            w.Write("ok");
        }
        using var server = new TestHttpServer(ms.ToArray()) { FileName = "pack.zip" };
        using var service = new SmartDownloadService(new AppSettings { DownloadDirectory = _dir, AutoExtract = true });
        var job = service.Add(server.Url);
        await service.Queue.WhenIdleAsync();
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.NotNull(job.ExtractedTo);
        Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(job.ExtractedTo!, "a.txt")));
    }
}

public class UpdateAndPortableTests
{
    private const string Release = """
    {"tag_name":"v0.5.0","html_url":"https://github.com/arst2020/Smart-Download-Manager/releases/tag/v0.5.0","draft":false,"prerelease":false,
     "body":"notes","assets":[
       {"name":"SmartDownloadManager-0.5.0-Setup.exe","browser_download_url":"https://github.com/arst2020/Smart-Download-Manager/releases/download/v0.5.0/SmartDownloadManager-0.5.0-Setup.exe"},
       {"name":"SmartDownloadManager-0.5.0-Portable.zip","browser_download_url":"https://github.com/arst2020/Smart-Download-Manager/releases/download/v0.5.0/SmartDownloadManager-0.5.0-Portable.zip"},
       {"name":"Evil-Setup.exe","browser_download_url":"https://evil.example.com/x-Setup.exe"}]}
    """;

    [Fact]
    public void Newer_release_is_found_with_trusted_assets_only()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Release);
        var info = UpdateChecker.Parse(doc.RootElement, new Version(0, 4, 0));
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 5, 0), info!.Version);
        Assert.True(info.InstallerUrl!.AbsoluteUri.EndsWith("0.5.0-Setup.exe"));
        Assert.NotNull(info.PortableUrl);
        Assert.Null(UpdateChecker.Parse(doc.RootElement, new Version(0, 5, 0)));
        Assert.False(UpdateChecker.IsTrusted(new Uri("https://github.com/someone-else/repo/releases/download/x.exe")));
    }

    [Fact]
    public async Task Check_reads_the_api_and_handles_no_release()
    {
        using var server = new TestHttpServer(System.Text.Encoding.UTF8.GetBytes(Release)) { FileName = "latest" };
        using var http = new HttpClient();
        var info = await new UpdateChecker(http, server.Url).CheckAsync(new Version(0, 1, 0));
        Assert.Equal("v0.5.0", info!.Tag);

        using var missing = new TestHttpServer(new byte[1]) { FileName = "latest", AlwaysStatus = 404 };
        Assert.Null(await new UpdateChecker(http, missing.Url).CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task Installer_is_verified_against_sha256sums()
    {
        byte[] setup = System.Text.Encoding.ASCII.GetBytes("installer bytes");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(setup)).ToLowerInvariant();
        string sums = $"{hash}  SmartDownloadManager-0.5.0-Setup.exe\n";
        bool tamper = false;
        var fake = new FakeHttp((req, _) => req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(sums) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(tamper ? new byte[] { 1, 2 } : setup) });
        string base_ = "https://github.com/arst2020/Smart-Download-Manager/releases/download/v0.5.0/";
        var info = new UpdateInfo(new Version(0, 5, 0), "v0.5.0", new Uri(base_), new Uri(base_ + "SmartDownloadManager-0.5.0-Setup.exe"), null, null)
            { ChecksumsUrl = new Uri(base_ + "SHA256SUMS.txt") };
        string dir = Path.Combine(Path.GetTempPath(), "sdm-upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checker = new UpdateChecker(fake.Client());
            string path = await checker.DownloadInstallerAsync(info, dir);
            Assert.Equal(setup, await File.ReadAllBytesAsync(path));

            tamper = true;
            File.Delete(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => checker.DownloadInstallerAsync(info, dir));
            Assert.False(File.Exists(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => checker.DownloadInstallerAsync(info with { ChecksumsUrl = null }, dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v0.4.0-beta", "0.4.0")]
    public void Tags_parse(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var v));
        Assert.Equal(expected, v.ToString());
    }

    [Fact]
    public void Data_directory_can_be_overridden()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sdm-data");
        AppPaths.OverrideDataDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "settings.json"), SettingsStore.DefaultPath);
            Assert.Equal(Path.Combine(dir, "downloads.json"), SDM.Core.Engine.DownloadHistory.DefaultPath);
        }
        finally { AppPaths.OverrideDataDirectory(null); }
    }
}
