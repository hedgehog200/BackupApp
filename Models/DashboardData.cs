namespace BackupApp.Models;

public class DashboardData
{
    public int TotalBackups { get; set; }
    public double SuccessRate { get; set; }
    public DateTime? NextBackup { get; set; }
    public int ActiveTargets { get; set; }
    public int OfflineTargets { get; set; }
    public int ActiveSources { get; set; }
    public List<BackupResult> RecentBackups { get; set; } = new();
    public List<LogEntry> RecentLogs { get; set; } = new();
    public List<string> BackupDates { get; set; } = new();
}
