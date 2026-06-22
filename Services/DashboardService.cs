using System.Text.RegularExpressions;
using BackupApp.Interfaces;
using BackupApp.Models;

namespace BackupApp.Services;

public class DashboardService : IDashboardService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly IScheduleCalculator _scheduleCalculator;

    public DashboardService(
        ISettingsService settingsService,
        ILogService logService,
        IScheduleCalculator scheduleCalculator)
    {
        _settingsService = settingsService;
        _logService = logService;
        _scheduleCalculator = scheduleCalculator;
    }

    public Task<DashboardData> GetDataAsync()
    {
        var settings = _settingsService.Load();
        var logs = _logService.GetRecent(10);

        var totalBackups = 0;
        var successCount = 0;
        var recentBackups = new List<BackupResult>();

        // Scan local storage for backup files
        var localPath = settings.LocalStorage?.Path;
        var backupDates = new HashSet<string>();

        if (!string.IsNullOrEmpty(localPath) && Directory.Exists(localPath))
        {
            var backupFiles = Directory.GetFiles(localPath, "Backup_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            totalBackups = backupFiles.Count;

            foreach (var file in backupFiles)
            {
                // Parse filename: Backup_{Name}_{yyyyMMdd_HHmmss}.zip
                var namePart = file.Name;
                var srcName = "";
                DateTime? ts = null;

                var match = Regex.Match(namePart, @"^Backup_(.+)_(\d{8}_\d{6})\.zip$");
                if (match.Success)
                {
                    srcName = match.Groups[1].Value;
                    if (DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMdd_HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    {
                        ts = parsed;
                    }
                }

                var dt = (ts ?? file.LastWriteTime).Date;
                backupDates.Add(dt.ToString("yyyy-MM-dd"));
                successCount++;

                if (recentBackups.Count < 20)
                {
                    var source = settings.Sources.FirstOrDefault(s => s.Name == srcName);
                    recentBackups.Add(new BackupResult
                    {
                        Id = Guid.NewGuid(),
                        SourceId = source?.Id ?? Guid.Empty,
                        StartedAt = ts ?? file.LastWriteTime,
                        CompletedAt = file.LastWriteTime,
                        Status = BackupStatus.Completed,
                        ArchivePath = file.FullName,
                        ArchiveSize = file.Length
                    });
                }
            }
        }

        // Also count encrypted backups
        if (!string.IsNullOrEmpty(localPath) && Directory.Exists(localPath))
        {
            var encFiles = Directory.GetFiles(localPath, "Backup_*.enc.zip");
            totalBackups += encFiles.Length;
        }

        var data = new DashboardData
        {
            TotalBackups = totalBackups,
            SuccessRate = totalBackups > 0 ? (successCount * 100.0 / totalBackups) : 100,
            ActiveTargets = settings.Targets.Count(t => t.IsEnabled && CheckBasicConnectivity(t.HostOrIp)),
            OfflineTargets = settings.Targets.Count(t => t.IsEnabled && !CheckBasicConnectivity(t.HostOrIp)),
            ActiveSources = settings.Sources.Count(s => s.IsEnabled),
            RecentBackups = recentBackups.Take(10).ToList(),
            RecentLogs = logs,
            BackupDates = backupDates.ToList()
        };

        if (settings.Schedules.Any())
        {
            var nextRun = settings.Schedules
                .Select(s => _scheduleCalculator.GetNextRunTime(s, s.LastRun ?? DateTime.Now))
                .Min();
            data.NextBackup = nextRun;
        }

        return Task.FromResult(data);
    }

    private static bool CheckBasicConnectivity(string hostOrIp)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = ping.Send(hostOrIp, 1000);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
