using System.Text.Json;
using BackupApp.Interfaces;
using BackupApp.Utils;

namespace BackupApp.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private AppSettings? _cached;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new ScheduleTypeJsonConverter() }
    };

    public SettingsService()
    {
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public AppSettings Load()
    {
        if (_cached != null)
            return _cached;

        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonHelper.Options) ?? new AppSettings();
                DecryptPasswords(_cached);
            }
            else
            {
                _cached = new AppSettings();
                Save(_cached);
            }
        }
        catch
        {
            _cached = new AppSettings();
        }

        return _cached;
    }

    public void Save(AppSettings settings)
    {
        var encryptCopy = CloneAndEncryptPasswords(settings);
        _cached = settings;
        var json = JsonSerializer.Serialize(encryptCopy, WriteOptions);
        File.WriteAllText(_settingsPath, json);
    }

    public void Export(string filePath)
    {
        var settings = Load();
        var json = JsonSerializer.Serialize(settings, WriteOptions);
        File.WriteAllText(filePath, json);
    }

    public void Import(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Файл конфигурации не найден", filePath);

        var json = File.ReadAllText(filePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonHelper.Options);
        if (settings != null)
        {
            Save(settings);
        }
    }

    private static void DecryptPasswords(AppSettings settings)
    {
        var targets = settings.Targets;
        if (targets != null)
        {
            foreach (var target in targets)
            {
                if (!string.IsNullOrEmpty(target.PasswordKey))
                {
                    try { target.PasswordKey = CryptoHelper.UnprotectString(target.PasswordKey); }
                    catch { /* не зашифровано или пользователь другой */ }
                }
            }
        }

        if (settings.Notifications?.Email != null &&
            !string.IsNullOrEmpty(settings.Notifications.Email.PasswordKey))
        {
            try { settings.Notifications.Email.PasswordKey = CryptoHelper.UnprotectString(settings.Notifications.Email.PasswordKey); }
            catch { }
        }

        if (settings.Sources != null)
        {
            foreach (var source in settings.Sources)
            {
                if (!string.IsNullOrEmpty(source.Password))
                {
                    try { source.Password = CryptoHelper.UnprotectString(source.Password); }
                    catch { }
                }
            }
        }
    }

    private static AppSettings CloneAndEncryptPasswords(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonHelper.Options);
        var clone = JsonSerializer.Deserialize<AppSettings>(json, JsonHelper.Options) ?? new AppSettings();

        if (clone.Targets != null)
        {
            foreach (var target in clone.Targets)
            {
                if (!string.IsNullOrEmpty(target.PasswordKey) && !target.PasswordKey.StartsWith("DPAPI|"))
                {
                    target.PasswordKey = CryptoHelper.ProtectString(target.PasswordKey);
                }
            }
        }

        if (clone.Notifications?.Email != null &&
            !string.IsNullOrEmpty(clone.Notifications.Email.PasswordKey) &&
            !clone.Notifications.Email.PasswordKey.StartsWith("DPAPI|"))
        {
            clone.Notifications.Email.PasswordKey = CryptoHelper.ProtectString(clone.Notifications.Email.PasswordKey);
        }

        if (clone.Sources != null)
        {
            foreach (var source in clone.Sources)
            {
                if (!string.IsNullOrEmpty(source.Password) && !source.Password.StartsWith("DPAPI|"))
                {
                    source.Password = CryptoHelper.ProtectString(source.Password);
                }
            }
        }

        return clone;
    }
}
