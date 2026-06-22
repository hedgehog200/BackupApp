namespace BackupApp.Models;

public class Schedule
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public ScheduleType Type { get; set; }
    public TimeSpan? TimeOfDay { get; set; }
    public int? IntervalHours { get; set; }
    public List<DayOfWeek> ActiveDays { get; set; } = new();
    public bool UseSmartCalculation { get; set; }
    public bool SkipIfNoChanges { get; set; }
    public DateTime? LastRun { get; set; }
}

public enum ScheduleType
{
    Daily,
    Weekly,
    Monthly,
    Interval,
    OnChange,
    Smart
}
