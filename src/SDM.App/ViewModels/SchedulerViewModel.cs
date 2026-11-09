using System.Globalization;
using SDM.App.Localization;
using SDM.Core;
using SDM.Core.Scheduling;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

public sealed class DayToggle : ObservableObject
{
    private bool _isOn;

    public DayToggle(DayOfWeek day, string label, bool isOn)
    {
        Day = day;
        Label = label;
        _isOn = isOn;
    }

    public DayOfWeek Day { get; }
    public string Label { get; }
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
}

public sealed class HourCell
{
    public required string Hour { get; init; }
    public required bool IsOn { get; init; }
}

/// <summary>Edits the schedule: time window, weekdays, speed limit and what happens after the queue.</summary>
public sealed class SchedulerViewModel : ObservableObject
{
    private readonly SmartDownloadService _service;
    private bool _enabled;
    private string _start;
    private string _stop;
    private bool _pauseAtEnd;
    private long _speedLimit;
    private AfterQueueAction _after;

    public SchedulerViewModel(SmartDownloadService service)
    {
        _service = service;
        var s = service.Settings.Schedule;
        _enabled = s.Enabled;
        _start = s.Start;
        _stop = s.Stop;
        _pauseAtEnd = s.PauseAtEnd;
        _speedLimit = s.SpeedLimitKBps;
        _after = s.AfterQueue;

        var culture = Loc.Instance.Culture;
        // Week starts on Saturday in the Persian calendar and on Monday in German/English locales.
        var order = culture.TwoLetterISOLanguageName == "fa"
            ? new[] { DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            : new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        foreach (var d in order)
        {
            var toggle = new DayToggle(d, culture.DateTimeFormat.GetDayName(d), s.Days.Count == 0 || s.Days.Contains(d));
            toggle.PropertyChanged += (_, _) => OnPropertyChanged(nameof(NextStartText));
            Days.Add(toggle);
        }
    }

    public List<DayToggle> Days { get; } = new();

    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) OnPropertyChanged(nameof(NextStartText)); } }

    public string Start
    {
        get => _start;
        set { if (Set(ref _start, value)) Changed(); }
    }

    public string Stop
    {
        get => _stop;
        set { if (Set(ref _stop, value)) Changed(); }
    }

    public bool PauseAtEnd { get => _pauseAtEnd; set => Set(ref _pauseAtEnd, value); }

    public IReadOnlyList<ChoiceOption<long>> SpeedLimits { get; } = new[]
    {
        new ChoiceOption<long>(0, Loc.Get("NoLimit")),
        new ChoiceOption<long>(10 * 1024, "10 MB/s"),
        new ChoiceOption<long>(5 * 1024, "5 MB/s"),
        new ChoiceOption<long>(2 * 1024, "2 MB/s"),
        new ChoiceOption<long>(1024, "1 MB/s"),
        new ChoiceOption<long>(512, "512 KB/s"),
    };

    public long SpeedLimit { get => _speedLimit; set => Set(ref _speedLimit, value); }

    public IReadOnlyList<ChoiceOption<AfterQueueAction>> AfterActions { get; } = new[]
    {
        new ChoiceOption<AfterQueueAction>(AfterQueueAction.Nothing, Loc.Get("AfterNothing")),
        new ChoiceOption<AfterQueueAction>(AfterQueueAction.Sleep, Loc.Get("AfterSleep")),
        new ChoiceOption<AfterQueueAction>(AfterQueueAction.Shutdown, Loc.Get("AfterShutdown")),
        new ChoiceOption<AfterQueueAction>(AfterQueueAction.ExitApp, Loc.Get("AfterExit")),
    };

    public AfterQueueAction After { get => _after; set => Set(ref _after, value); }

    public bool IsValid => IsTime(Start) && IsTime(Stop);

    /// <summary>24 cells for the timeline (ignores weekdays).</summary>
    public IReadOnlyList<HourCell> Hours
    {
        get
        {
            var probe = new ScheduleSettings { Enabled = true, Start = Start, Stop = Stop, Days = new() };
            var day = DateTime.Today;
            return Enumerable.Range(0, 24).Select(h => new HourCell
            {
                Hour = h % 4 == 0 ? h.ToString("00", CultureInfo.InvariantCulture) : "",
                IsOn = IsValid && ScheduleWindow.IsOpen(probe, day.AddHours(h).AddMinutes(30))
            }).ToList();
        }
    }

    public string NextStartText
    {
        get
        {
            if (!Enabled) return Loc.Get("ScheduleOff");
            if (!IsValid) return Loc.Get("TimeFormat");
            var next = ScheduleWindow.NextStart(ToSchedule(), DateTime.Now);
            if (next is null) return Loc.Get("NoDaySelected");
            int waiting = _service.Queue.Jobs.Count(j => j.IsScheduled && j.Status == SDM.Core.Models.DownloadStatus.Scheduled);
            string when = next.Value <= DateTime.Now
                ? Loc.Get("ScheduleOpenNow")
                : next.Value.ToString("dddd HH:mm", Loc.Instance.Culture);
            return string.Format(Loc.Get("NextStartLine"), when, waiting);
        }
    }

    public ScheduleSettings ToSchedule() => new()
    {
        Enabled = Enabled,
        Start = Normalize(Start),
        Stop = Normalize(Stop),
        Days = Days.Where(d => d.IsOn).Select(d => d.Day).ToList(),
        PauseAtEnd = PauseAtEnd,
        SpeedLimitKBps = SpeedLimit,
        AfterQueue = After,
    };

    private void Changed()
    {
        OnPropertyChanged(nameof(Hours));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(NextStartText));
    }

    private static bool IsTime(string s) =>
        TimeSpan.TryParseExact(s?.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1);

    private static string Normalize(string s) => ScheduleSettings.ParseTime(FileNameHelper.NormalizeDigits(s)).ToString(@"hh\:mm");
}
