using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface IDashboardService
{
    Task<DashboardData> GetDataAsync();
}
