using System.Globalization;

namespace SDM.Core.Scheduling;

public enum AfterQueueAction
{
    Nothing,
    Sleep,
    Shutdown,
    ExitApp
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }

    /// <summary>"HH:mm", local time. A window may cross midnight (e.g. 23:00 → 07:00).</summary>
    public string Start { get; set; } = "02:00";

    public string Stop { get; set; } = "08:00";

    /// <summary>Days on which the window STARTS. Empty = every day.</summary>
    public List<DayOfWeek> Days { get; set; } = new()
    {
        DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday
    };

    /// <summary>Speed limit while the window is open, KB/s; 0 = no limit.</summary>
    public long SpeedLimitKBps { get; set; }

    /// <summary>Pause unfinished scheduled downloads when the window closes (they resume next window).</summary>
    public bool PauseAtEnd { get; set; } = true;

    public AfterQueueAction AfterQueue { get; set; } = AfterQueueAction.Nothing;

    public static TimeSpan ParseTime(string s) =>
        TimeSpan.TryParseExact(s?.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t)
            ? t
            : TimeSpan.Zero;
}

/// <summary>Pure time-window logic (no timers), so it can be tested with any clock.</summary>
public static class ScheduleWindow
{
    public static bool IsOpen(ScheduleSettings s, DateTime now)
    {
        if (!s.Enabled) return false;
        TimeSpan start = ScheduleSettings.ParseTime(s.Start), stop = ScheduleSettings.ParseTime(s.Stop);
        if (start == stop) return DayAllowed(s, now.DayOfWeek); // whole day

        if (start < stop)
            return DayAllowed(s, now.DayOfWeek) && now.TimeOfDay >= start && now.TimeOfDay < stop;

        // Crosses midnight: evening part belongs to today, morning part to yesterday's window.
        if (now.TimeOfDay >= start) return DayAllowed(s, now.DayOfWeek);
        if (now.TimeOfDay < stop) return DayAllowed(s, now.AddDays(-1).DayOfWeek);
        return false;
    }

    /// <summary>Next moment the window opens (now if it is open); null if no day is allowed.</summary>
    public static DateTime? NextStart(ScheduleSettings s, DateTime now)
    {
        if (!s.Enabled) return null;
        if (IsOpen(s, now)) return now;
        TimeSpan start = ScheduleSettings.ParseTime(s.Start);
        for (int i = 0; i <= 7; i++)
        {
            DateTime candidate = now.Date.AddDays(i) + start;
            if (candidate > now && DayAllowed(s, candidate.DayOfWeek)) return candidate;
        }
        return null;
    }

    private static bool DayAllowed(ScheduleSettings s, DayOfWeek d) => s.Days.Count == 0 || s.Days.Contains(d);
}
