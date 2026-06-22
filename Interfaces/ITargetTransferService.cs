using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface ITargetTransferService
{
    Task<TargetTransferResult> TransferAsync(
        string archivePath,
        TargetPC target,
        IProgress<TransferProgress> progress,
        CancellationToken ct);

    Task<bool> CheckAvailabilityAsync(TargetPC target);
}
