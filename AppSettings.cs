using BackupApp.Models;

namespace BackupApp;

public class AppSettings
{
    public AppConfig App { get; set; } = new();
    public List<BackupSource> Sources { get; set; } = new();
    public List<Schedule> Schedules { get; set; } = new();
    public List<TargetPC> Targets { get; set; } = new();
    public LocalStorageConfig LocalStorage { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
}

public class AppConfig
{
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "dark";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Shift+B";
}

public class LocalStorageConfig
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "C:\\Backups\\Local";
    public RetentionPolicy Retention { get; set; } = new() { KeepLastN = 5, KeepDays = 30 };
}

public class NotificationConfig
{
    public bool Toast { get; set; } = true;
    public EmailConfig Email { get; set; } = new();
}

public class EmailConfig
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string PasswordKey { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
}

public class NetworkConfig
{
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxParallelTransfers { get; set; } = 3;
    public int RetryCount { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 5000;
}
