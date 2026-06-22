namespace BackupApp.Interfaces;

public interface INotificationService
{
    Task ShowToastAsync(string title, string message);
    Task SendEmailAsync(string subject, string body, bool isError = false);
}
