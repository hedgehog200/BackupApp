namespace BackupApp.Models;

public class BackupProgress
{
    public string SourceName { get; set; } = string.Empty;
    public int Percentage { get; set; }
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
}

public class TransferProgress
{
    public string TargetName { get; set; } = string.Empty;
    public int Percentage { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
}
