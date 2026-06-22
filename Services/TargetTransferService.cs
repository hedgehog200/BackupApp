using System.Diagnostics;
using System.Net.NetworkInformation;
using BackupApp.Interfaces;
using BackupApp.Models;
using BackupApp.Utils;

namespace BackupApp.Services;

public class TargetTransferService : ITargetTransferService
{
    private readonly ILogService _log;
    private readonly ISettingsService _settingsService;
    private readonly IRetentionManager _retentionManager;

    public TargetTransferService(
        ILogService log,
        ISettingsService settingsService,
        IRetentionManager retentionManager)
    {
        _log = log;
        _settingsService = settingsService;
        _retentionManager = retentionManager;
    }

    public async Task<bool> CheckAvailabilityAsync(TargetPC target)
    {
        try
        {
            if (string.IsNullOrEmpty(target.NetworkPath))
            {
                _log.Warning($"Сетевой путь не указан для {target.Name}", "TargetTransfer");
                return false;
            }

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target.HostOrIp, 3000);
            if (reply.Status != IPStatus.Success)
            {
                _log.Warning($"Ping failed для {target.Name} ({target.HostOrIp}): {reply.Status}", "TargetTransfer");
                return false;
            }

            return Directory.Exists(target.NetworkPath);
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка проверки доступности {target.Name}: {ex.Message}", "TargetTransfer");
            return false;
        }
    }

    public async Task<TargetTransferResult> TransferAsync(
        string archivePath,
        TargetPC target,
        IProgress<TransferProgress> progress,
        CancellationToken ct)
    {
        var result = new TargetTransferResult
        {
            TargetId = target.Id,
            TargetName = target.Name,
            StartedAt = DateTime.Now
        };

        var settings = _settingsService.Load();
        int maxRetries = settings.Network.RetryCount;
        int retryDelay = settings.Network.RetryDelayMs;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (attempt > 0)
                {
                    _log.Info($"Повторная попытка {attempt}/{maxRetries} для {target.Name}", "TargetTransfer");
                    await Task.Delay(retryDelay, ct);
                }

                if (!File.Exists(archivePath))
                    throw new FileNotFoundException($"Архив не найден: {archivePath}");

                var fileInfo = new FileInfo(archivePath);
                result.TotalBytes = fileInfo.Length;

                var available = await CheckAvailabilityAsync(target);
                if (!available)
                    throw new InvalidOperationException($"Целевой ПК недоступен: {target.Name}");

                if (string.IsNullOrEmpty(target.NetworkPath))
                    throw new InvalidOperationException($"Сетевой путь не указан для целевого ПК: {target.Name}");

                var destDir = target.NetworkPath;
                Directory.CreateDirectory(destDir);

                var destPath = Path.Combine(destDir, Path.GetFileName(archivePath));
                var tempDestPath = destPath + ".tmp";

                _log.Info($"Передача на {target.Name}: {destPath} (попытка {attempt + 1})", "TargetTransfer");

                using var sourceStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
                using var destStream = new FileStream(tempDestPath, FileMode.Create, FileAccess.Write);

                var buffer = new byte[65536];
                long totalBytesWritten = 0;
                var stopwatch = Stopwatch.StartNew();
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await destStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalBytesWritten += bytesRead;

                    var elapsed = stopwatch.Elapsed.TotalSeconds;
                    var speed = elapsed > 0 ? totalBytesWritten / elapsed : 0;

                    progress?.Report(new TransferProgress
                    {
                        TargetName = target.Name,
                        Percentage = (int)(totalBytesWritten * 100 / result.TotalBytes),
                        BytesTransferred = totalBytesWritten,
                        TotalBytes = result.TotalBytes,
                        SpeedBytesPerSec = speed
                    });
                }

                stopwatch.Stop();
                destStream.Close();

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempDestPath, destPath);

                result.BytesTransferred = totalBytesWritten;
                result.SpeedBytesPerSec = totalBytesWritten / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                result.Success = true;
                result.CompletedAt = DateTime.Now;

                _log.Info($"Передача на {target.Name} завершена: {FormatSize(result.BytesTransferred)} за {stopwatch.Elapsed.TotalSeconds:F1}с", "TargetTransfer");

                var sha256 = CryptoHelper.ComputeSha256(destPath);
                _log.Info($"Контрольная сумма SHA256: {sha256}", "TargetTransfer");

                if (target.Retention.KeepLastN.HasValue || target.Retention.KeepDays.HasValue)
                {
                    await _retentionManager.CleanupAsync(destDir, target.Retention);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Передача отменена";
                _log.Warning($"Передача на {target.Name} отменена", "TargetTransfer");
                break;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                _log.Error($"Ошибка передачи на {target.Name} (попытка {attempt + 1}): {ex.Message}", "TargetTransfer");

                try
                {
                    var tempPath = Path.Combine(target.NetworkPath, Path.GetFileName(archivePath) + ".tmp");
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { }
            }
        }

        return result;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
