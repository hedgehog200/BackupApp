namespace BackupApp.Models;

public class WebMessageRequest
{
    public int RequestId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, object>? Data { get; set; }
}
