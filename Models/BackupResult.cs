namespace BackupApp.Models;

public class BackupResult
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public BackupStatus Status { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public long ArchiveSize { get; set; }
    public string? ErrorMessage { get; set; }
    public List<TargetTransferResult> Transfers { get; set; } = new();
}

public enum BackupStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    PartiallyFailed
}
