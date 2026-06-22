using BackupApp.Interfaces;
using BackupApp.Models;

namespace BackupApp.Services;

public class SmartScheduleCalculator : IScheduleCalculator
{
    public Schedule CalculateOptimalSchedule(string sourcePath, int analysisDays = 7)
    {
        var cutoff = DateTime.Now.AddDays(-analysisDays);
        var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                             .Where(f => File.GetLastWriteTime(f) > cutoff);

        var changesByHour = files
            .Select(f => File.GetLastWriteTime(f))
            .GroupBy(dt => dt.Hour)
            .Select(g => new { Hour = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        if (!changesByHour.Any())
        {
            return new Schedule
            {
                Type = ScheduleType.Weekly,
                IntervalHours = 168,
                TimeOfDay = TimeSpan.FromHours(2),
                ActiveDays = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                UseSmartCalculation = true,
                SkipIfNoChanges = true
            };
        }

        var avgChangesPerHour = changesByHour.Average(x => x.Count);
        var hotHours = changesByHour
            .Where(x => x.Count > avgChangesPerHour * 1.5)
            .Select(x => x.Hour)
            .ToList();

        int intervalHours = avgChangesPerHour switch
        {
            > 50 => 1,
            > 10 => 4,
            > 1 => 24,
            _ => 168
        };

        var recommendedTime = hotHours.Any()
            ? TimeSpan.FromHours((hotHours.Max() + 2) % 24)
            : TimeSpan.FromHours(2);

        var schedule = new Schedule
        {
            Type = intervalHours == 168 ? ScheduleType.Weekly : ScheduleType.Interval,
            IntervalHours = intervalHours,
            TimeOfDay = recommendedTime,
            ActiveDays = intervalHours == 168
                ? new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                : new(),
            UseSmartCalculation = true,
            SkipIfNoChanges = true
        };

        if (intervalHours == 24)
        {
            schedule.Type = ScheduleType.Daily;
        }

        return schedule;
    }

    public bool HasChangesSince(string sourcePath, DateTime since)
    {
        return Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Any(f => File.GetLastWriteTime(f) > since);
    }

    public DateTime GetNextRunTime(Schedule schedule, DateTime from)
    {
        DateTime next;

        switch (schedule.Type)
        {
            case ScheduleType.Interval when schedule.IntervalHours.HasValue:
                next = from.AddHours(schedule.IntervalHours.Value);
                break;

            case ScheduleType.Daily when schedule.TimeOfDay.HasValue:
                next = from.Date + schedule.TimeOfDay.Value;
                if (next <= from) next = next.AddDays(1);
                break;

            case ScheduleType.Weekly when schedule.TimeOfDay.HasValue:
                next = from.Date + schedule.TimeOfDay.Value;
                if (next <= from) next = next.AddDays(1);
                while (schedule.ActiveDays.Count > 0 && !schedule.ActiveDays.Contains(next.DayOfWeek))
                    next = next.AddDays(1);
                break;

            case ScheduleType.Monthly:
                next = new DateTime(from.Year, from.Month, 1).AddMonths(1);
                if (schedule.TimeOfDay.HasValue)
                    next = next.Date + schedule.TimeOfDay.Value;
                break;

            default:
                next = from.AddDays(1);
                break;
        }

        while (schedule.ActiveDays.Count > 0 && !schedule.ActiveDays.Contains(next.DayOfWeek))
            next = next.AddDays(1);

        if (schedule.TimeOfDay.HasValue && schedule.Type != ScheduleType.Interval)
        {
            var windowStart = schedule.TimeOfDay.Value;
            var windowEnd = windowStart.Add(TimeSpan.FromHours(4));
            var timeOfDay = next.TimeOfDay;

            if (timeOfDay < windowStart || timeOfDay > windowEnd)
            {
                if (schedule.Type == ScheduleType.Daily)
                    next = next.Date + windowStart;
            }
        }

        return next;
    }
}
