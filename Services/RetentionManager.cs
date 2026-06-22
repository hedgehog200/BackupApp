using BackupApp.Interfaces;
using BackupApp.Models;

namespace BackupApp.Services;

public class RetentionManager : IRetentionManager
{
    private readonly ILogService _log;

    public RetentionManager(ILogService log)
    {
        _log = log;
    }

    public Task CleanupAsync(string directoryPath, RetentionPolicy policy)
    {
        if (!Directory.Exists(directoryPath))
            return Task.CompletedTask;

        var backupFiles = Directory.GetFiles(directoryPath, "Backup_*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        int removed = 0;

        if (policy.KeepLastN.HasValue && backupFiles.Count > policy.KeepLastN.Value)
        {
            var toRemove = backupFiles.Skip(policy.KeepLastN.Value);
            foreach (var file in toRemove)
            {
                try
                {
                    file.Delete();
                    removed++;
                    _log.Info($"Удалён старый бэкап: {file.Name}", "Retention");
                }
                catch (Exception ex)
                {
                    _log.Error($"Ошибка удаления {file.Name}: {ex.Message}", "Retention");
                }
            }
        }

        if (policy.KeepDays.HasValue)
        {
            var cutoff = DateTime.Now.AddDays(-policy.KeepDays.Value);
            var oldFiles = backupFiles.Where(f => f.LastWriteTime < cutoff);
            foreach (var file in oldFiles)
            {
                try
                {
                    file.Delete();
                    removed++;
                    _log.Info($"Удалён просроченный бэкап: {file.Name}", "Retention");
                }
                catch (Exception ex)
                {
                    _log.Error($"Ошибка удаления {file.Name}: {ex.Message}", "Retention");
                }
            }
        }

        if (removed > 0)
        {
            _log.Info($"Ротация завершена: удалено {removed} файлов в {directoryPath}", "Retention");
        }

        return Task.CompletedTask;
    }
}
