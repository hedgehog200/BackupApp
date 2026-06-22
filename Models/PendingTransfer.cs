namespace BackupApp.Models;

public class PendingTransfer
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string ArchiveName { get; set; } = string.Empty;
    public DateTime QueuedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTime? LastAttempt { get; set; }
    public string? LastError { get; set; }
}
