using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface IScheduleCalculator
{
    Schedule CalculateOptimalSchedule(string sourcePath, int analysisDays = 7);
    DateTime GetNextRunTime(Schedule schedule, DateTime from);
    bool HasChangesSince(string sourcePath, DateTime since);
}
