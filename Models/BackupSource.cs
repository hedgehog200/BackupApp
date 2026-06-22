namespace BackupApp.Models;

public class BackupSource
{
    public Guid Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> ExcludePatterns { get; set; } = new();
    public int CompressionLevel { get; set; } = 6;
    public bool IsEnabled { get; set; } = true;
    public string? Password { get; set; }
}
