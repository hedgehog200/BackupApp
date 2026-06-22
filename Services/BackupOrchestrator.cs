using System.Text.Json;
using BackupApp.Interfaces;
using BackupApp.Models;
using BackupApp.Utils;

namespace BackupApp.Services;

public class BackupOrchestrator : IBackupOrchestrator
{
    private readonly ISettingsService _settingsService;
    private readonly IBackupEngine _backupEngine;
    private readonly ITargetTransferService _transferService;
    private readonly IScheduleCalculator _scheduleCalculator;
    private readonly ILogService _log;
    private readonly INotificationService _notificationService;

    private CancellationTokenSource? _cts;
    private List<PendingTransfer> _pendingQueue = new();
    private System.Threading.Timer? _queueTimer;
    private readonly string _queueFilePath;

    public BackupOrchestrator(
        ISettingsService settingsService,
        IBackupEngine backupEngine,
        ITargetTransferService transferService,
        IScheduleCalculator scheduleCalculator,
        ILogService log,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _backupEngine = backupEngine;
        _transferService = transferService;
        _scheduleCalculator = scheduleCalculator;
        _log = log;
        _notificationService = notificationService;
        _queueFilePath = Path.Combine(AppContext.BaseDirectory, "pending_queue.json");
    }

    public async Task<BackupResult> RunBackupAsync(Guid sourceId, CancellationToken ct = default, IProgress<BackupProgress>? progress = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var settings = _settingsService.Load();
        var source = settings.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source == null)
            throw new ArgumentException($"Источник не найден: {sourceId}");

        var schedule = settings.Schedules.FirstOrDefault(s => s.SourceId == sourceId);
        if (schedule != null && schedule.SkipIfNoChanges)
        {
            var lastBackup = FindLastBackupTime(sourceId);
            if (lastBackup.HasValue && !_scheduleCalculator.HasChangesSince(source.Path, lastBackup.Value))
            {
                _log.Info($"Пропуск {source.Name}: нет изменений", "Orchestrator");
                return new BackupResult
                {
                    SourceId = sourceId,
                    Status = BackupStatus.Completed,
                    StartedAt = DateTime.Now,
                    CompletedAt = DateTime.Now
                };
            }
        }

        _log.Info($"Запуск бэкапа: {source.Name}", "Orchestrator");

        var tempPath = Path.Combine(Path.GetTempPath(), "BackupApp", Guid.NewGuid().ToString());
        var archiveProgress = progress ?? new Progress<BackupProgress>();
        var archiveResult = await _backupEngine.CreateBackupAsync(source, tempPath, archiveProgress, token);

        if (archiveResult.Status != BackupStatus.Completed || string.IsNullOrEmpty(archiveResult.ArchivePath))
        {
            var errorMsg = archiveResult.ErrorMessage ?? "Неизвестная ошибка";
            await _notificationService.ShowToastAsync("Бэкап не удался", $"{source.Name}: {errorMsg}");
            await _notificationService.SendEmailAsync($"Бэкап не удался: {source.Name}", errorMsg, true);
            return archiveResult;
        }

        var enabledTargets = settings.Targets.Where(t => t.IsEnabled).ToList();
        if (!enabledTargets.Any())
        {
            archiveResult.Status = BackupStatus.Completed;
            _log.Info($"Бэкап {source.Name} завершён (нет целевых ПК)", "Orchestrator");
            return archiveResult;
        }

        var sha256 = CryptoHelper.ComputeSha256(archiveResult.ArchivePath);
        _log.Info($"SHA256 архива: {sha256}", "Orchestrator");

        var transferTasks = new List<Task<TargetTransferResult>>();
        var maxParallel = settings.Network.MaxParallelTransfers;
        using var semaphore = new SemaphoreSlim(maxParallel);

        foreach (var target in enabledTargets)
        {
            await semaphore.WaitAsync(token);
            transferTasks.Add(ProcessTargetTransferAsync(archiveResult.ArchivePath, target, sourceId, semaphore, token));
        }

        var transferResults = await Task.WhenAll(transferTasks);
        archiveResult.Transfers.AddRange(transferResults);

        var failedTransfers = transferResults.Where(r => !r.Success).ToList();
        if (failedTransfers.Any())
        {
            archiveResult.Status = BackupStatus.PartiallyFailed;

            foreach (var failed in failedTransfers)
            {
                _log.Error($"Не удалась передача на {failed.TargetName}: {failed.ErrorMessage}", "Orchestrator");
            }
        }

        archiveResult.CompletedAt = DateTime.Now;
        _log.Info($"Бэкап {source.Name} завершён. Статус: {archiveResult.Status}", "Orchestrator");

        if (failedTransfers.Count == 0)
        {
            await _notificationService.ShowToastAsync("Бэкап выполнен", $"Источник {source.Name} — {enabledTargets.Count} ПК");
            await _notificationService.SendEmailAsync($"Бэкап выполнен: {source.Name}",
                $"Успешно скопирован на {enabledTargets.Count} ПК ({FormatSize(archiveResult.ArchiveSize)})");
        }
        else
        {
            await _notificationService.ShowToastAsync("Бэкап с ошибками", $"{source.Name}: {failedTransfers.Count} ошибок");
            await _notificationService.SendEmailAsync($"Бэкап с ошибками: {source.Name}",
                $"{failedTransfers.Count} из {enabledTargets.Count} ПК не получены", true);
        }

        try { if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true); } catch { }

        return archiveResult;
    }

    private async Task<TargetTransferResult> ProcessTargetTransferAsync(
        string archivePath, TargetPC target, Guid sourceId,
        SemaphoreSlim semaphore, CancellationToken token)
    {
        try
        {
            var targetProgress = new Progress<TransferProgress>();
            var result = await _transferService.TransferAsync(archivePath, target, targetProgress, token);

            if (!result.Success)
            {
                _pendingQueue.Add(new PendingTransfer
                {
                    Id = Guid.NewGuid(),
                    SourceId = sourceId,
                    TargetId = target.Id,
                    ArchivePath = archivePath,
                    ArchiveName = Path.GetFileName(archivePath),
                    QueuedAt = DateTime.Now,
                    LastError = result.ErrorMessage
                });
                await SaveQueueAsync();
                _log.Info($"Передача {target.Name} добавлена в очередь ожидания", "Orchestrator");
            }

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task ProcessPendingTransfersAsync()
    {
        if (_pendingQueue.Count == 0) return;

        _log.Info($"Обработка очереди: {_pendingQueue.Count} ожидающих передач", "Orchestrator");
        var settings = _settingsService.Load();
        var remaining = new List<PendingTransfer>();

        foreach (var pending in _pendingQueue)
        {
            var target = settings.Targets.FirstOrDefault(t => t.Id == pending.TargetId);
            if (target == null || !target.IsEnabled)
            {
                remaining.Add(pending);
                continue;
            }

            var available = await _transferService.CheckAvailabilityAsync(target);
            if (!available)
            {
                pending.LastAttempt = DateTime.Now;
                pending.RetryCount++;
                remaining.Add(pending);
                continue;
            }

            try
            {
                _log.Info($"Отправка из очереди: {pending.ArchiveName} -> {target.Name}", "Orchestrator");
                var progress = new Progress<TransferProgress>();
                var result = await _transferService.TransferAsync(pending.ArchivePath, target, progress, CancellationToken.None);

                if (result.Success)
                {
                    _log.Info($"Очередь: передача {pending.ArchiveName} на {target.Name} выполнена", "Orchestrator");
                }
                else
                {
                    pending.RetryCount++;
                    pending.LastError = result.ErrorMessage;
                    if (pending.RetryCount < 10)
                        remaining.Add(pending);
                    else
                        _log.Error($"Очередь: превышено число попыток для {pending.ArchiveName}", "Orchestrator");
                }
            }
            catch (Exception ex)
            {
                pending.RetryCount++;
                pending.LastError = ex.Message;
                if (pending.RetryCount < 10)
                    remaining.Add(pending);
            }
        }

        _pendingQueue = remaining;
        await SaveQueueAsync();
    }

    public List<PendingTransfer> GetPendingTransfers() => _pendingQueue.ToList();

    public async Task SaveQueueAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_pendingQueue);
            await File.WriteAllTextAsync(_queueFilePath, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка сохранения очереди: {ex.Message}", "Orchestrator");
        }
    }

    public async Task LoadQueueAsync()
    {
        try
        {
            if (File.Exists(_queueFilePath))
            {
                var json = await File.ReadAllTextAsync(_queueFilePath);
                _pendingQueue = JsonSerializer.Deserialize<List<PendingTransfer>>(json) ?? new();
                _log.Info($"Загружено {_pendingQueue.Count} ожидающих передач", "Orchestrator");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка загрузки очереди: {ex.Message}", "Orchestrator");
            _pendingQueue = new();
        }
    }

    public Task StartSchedulerAsync()
    {
        _log.Info("Планировщик запущен", "Scheduler");

        _ = LoadQueueAsync();

        _queueTimer = new System.Threading.Timer(async _ =>
        {
            try { await ProcessPendingTransfersAsync(); }
            catch (Exception ex) { _log.Error($"Ошибка обработки очереди: {ex.Message}", "Scheduler"); }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

        return Task.CompletedTask;
    }

    public void StopScheduler()
    {
        _cts?.Cancel();
        _queueTimer?.Dispose();
        _log.Info("Планировщик остановлен", "Scheduler");
    }

    private DateTime? FindLastBackupTime(Guid sourceId)
    {
        var settings = _settingsService.Load();
        var source = settings.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source == null) return null;

        var localPath = settings.LocalStorage.Path;
        if (!Directory.Exists(localPath)) return null;

        var files = Directory.GetFiles(localPath, $"Backup_{source.Name}_*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        return files.FirstOrDefault()?.LastWriteTime;
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
