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
