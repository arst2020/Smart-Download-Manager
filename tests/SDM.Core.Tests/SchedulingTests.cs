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
