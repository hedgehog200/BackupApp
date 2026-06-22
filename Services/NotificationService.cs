using System.Net;
using System.Net.Mail;
using BackupApp.Interfaces;

namespace BackupApp.Services;

public class NotificationService : INotificationService
{
    private readonly ILogService _log;
    private readonly ISettingsService _settingsService;

    public NotificationService(ILogService log, ISettingsService settingsService)
    {
        _log = log;
        _settingsService = settingsService;
    }

    public Task ShowToastAsync(string title, string message)
    {
        try
        {
            var settings = _settingsService.Load();
            if (!settings.Notifications.Toast)
                return Task.CompletedTask;

            _log.Info($"Toast: {title} — {message}", "Notification");
        }
        catch (Exception ex)
        {
            _log.Warning($"Ошибка Toast: {ex.Message}", "Notification");
        }

        return Task.CompletedTask;
    }

    public async Task SendEmailAsync(string subject, string body, bool isError = false)
    {
        var settings = _settingsService.Load();
        var emailConfig = settings.Notifications.Email;

        if (!emailConfig.Enabled || string.IsNullOrEmpty(emailConfig.SmtpHost))
            return;

        try
        {
            using var client = new SmtpClient(emailConfig.SmtpHost, emailConfig.SmtpPort)
            {
                EnableSsl = emailConfig.UseSsl,
                Credentials = string.IsNullOrEmpty(emailConfig.Username)
                    ? null
                    : new NetworkCredential(emailConfig.Username, emailConfig.PasswordKey)
            };

            await client.SendMailAsync(
                emailConfig.Username,
                emailConfig.Username,
                $"[BackupApp] {subject}",
                body);

            _log.Info($"Email отправлен: {subject}", "Notification");
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка отправки email: {ex.Message}", "Notification");
        }
    }
}
