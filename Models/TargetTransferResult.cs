namespace BackupApp.Models;

public class TargetTransferResult
{
    public Guid TargetId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
