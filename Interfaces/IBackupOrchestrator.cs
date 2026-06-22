using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface IBackupOrchestrator
{
    Task<BackupResult> RunBackupAsync(Guid sourceId, CancellationToken ct = default, IProgress<BackupProgress>? progress = null);
    Task StartSchedulerAsync();
    void StopScheduler();
    Task ProcessPendingTransfersAsync();
    List<PendingTransfer> GetPendingTransfers();
    Task SaveQueueAsync();
    Task LoadQueueAsync();
}
