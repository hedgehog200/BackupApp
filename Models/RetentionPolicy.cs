namespace BackupApp.Models;

public class RetentionPolicy
{
    public int? KeepLastN { get; set; }
    public int? KeepDays { get; set; }
}
