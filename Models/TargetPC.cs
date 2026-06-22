namespace BackupApp.Models;

public class TargetPC
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HostOrIp { get; set; } = string.Empty;
    public string NetworkPath { get; set; } = string.Empty;
    public bool UseCustomCredentials { get; set; }
    public string? Username { get; set; }
    public string? PasswordKey { get; set; }
    public RetentionPolicy Retention { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
}
