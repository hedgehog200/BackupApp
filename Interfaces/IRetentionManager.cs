using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface IRetentionManager
{
    Task CleanupAsync(string directoryPath, RetentionPolicy policy);
}
