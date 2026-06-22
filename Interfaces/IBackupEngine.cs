using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface IBackupEngine
{
    Task<BackupResult> CreateBackupAsync(
        BackupSource source,
        string outputPath,
        IProgress<BackupProgress> progress,
        CancellationToken ct);
}
