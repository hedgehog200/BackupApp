using System.Collections.Concurrent;
using BackupApp.Interfaces;
using BackupApp.Models;
namespace BackupApp.Services;

public class BackupSchedulerService : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IScheduleCalculator _calculator;
    private readonly IBackupOrchestrator _orchestrator;
    private readonly ILogService _log;
    private System.Threading.Timer? _timer;
    private readonly ConcurrentDictionary<Guid, bool> _runningBackups = new();

    public BackupSchedulerService(
        ISettingsService settings,
        IScheduleCalculator calculator,
        IBackupOrchestrator orchestrator,
        ILogService log)
    {
        _settings = settings;
        _calculator = calculator;
        _orchestrator = orchestrator;
        _log = log;
    }

    public bool TryStartBackup(Guid sourceId) => _runningBackups.TryAdd(sourceId, true);
    public void FinishBackup(Guid sourceId) => _runningBackups.TryRemove(sourceId, out _);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.Info("Планировщик запущен", "Scheduler");
        _timer = new System.Threading.Timer(async _ =>
        {
            try { await CheckSchedulesAsync(); }
            catch (Exception ex) { _log.Error($"Ошибка планировщика: {ex.Message}", "Scheduler"); }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Info("Планировщик остановлен", "Scheduler");
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private async Task CheckSchedulesAsync()
    {
        var settings = _settings.Load();
        if (settings.Schedules == null || !settings.Schedules.Any()) return;

        foreach (var schedule in settings.Schedules)
        {
            if (schedule.Type == ScheduleType.OnChange) continue;

            var lastRun = schedule.LastRun ?? DateTime.MinValue;
            var nextRun = _calculator.GetNextRunTime(schedule, lastRun);

            if (nextRun <= DateTime.Now)
            {
                var source = settings.Sources.FirstOrDefault(s => s.Id == schedule.SourceId);
                if (source == null || !source.IsEnabled) continue;

                if (schedule.SkipIfNoChanges &&
                    lastRun != DateTime.MinValue &&
                    !_calculator.HasChangesSince(source.Path, lastRun))
                {
                    _log.Info($"Планировщик: пропуск {source.Name} — нет изменений", "Scheduler");
                    schedule.LastRun = DateTime.Now;
                    continue;
                }

                if (!_runningBackups.TryAdd(schedule.SourceId, true))
                {
                    _log.Info($"Планировщик: пропуск {source.Name} — бэкап уже выполняется", "Scheduler");
                    continue;
                }

                _log.Info($"Планировщик: запуск бэкапа {source.Name}", "Scheduler");

                try
                {
                    await _orchestrator.RunBackupAsync(schedule.SourceId);
                }
                catch (Exception ex)
                {
                    _log.Error($"Планировщик: ошибка бэкапа {source.Name}: {ex.Message}", "Scheduler");
                }
                finally
                {
                    _runningBackups.TryRemove(schedule.SourceId, out _);
                }

                schedule.LastRun = DateTime.Now;
                _settings.Save(settings);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
