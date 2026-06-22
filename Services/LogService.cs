using System.Text.Json;
using BackupApp.Interfaces;
using BackupApp.Models;
using Serilog;

namespace BackupApp.Services;

public class LogService : ILogService, IDisposable
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private const int MaxEntries = 10000;

    public LogService()
    {
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "backup-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31)
            .CreateLogger();

        Info("Сервис логирования инициализирован", "LogService");
    }

    public void Info(string message, string? source = null)
        => AddEntry(LogLevel.Info, message, source);

    public void Warning(string message, string? source = null)
        => AddEntry(LogLevel.Warning, message, source);

    public void Error(string message, string? source = null)
        => AddEntry(LogLevel.Error, message, source);

    private void AddEntry(LogLevel level, string message, string? source)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
            Source = source
        };

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }

        switch (level)
        {
            case LogLevel.Info:
                _logger.Information("[{Source}] {Message}", source, message);
                break;
            case LogLevel.Warning:
                _logger.Warning("[{Source}] {Message}", source, message);
                break;
            case LogLevel.Error:
                _logger.Error("[{Source}] {Message}", source, message);
                break;
        }
    }

    public List<LogEntry> GetRecent(int count = 100)
    {
        lock (_lock)
        {
            return _entries.TakeLast(count).ToList();
        }
    }

    public List<LogEntry> GetFiltered(LogLevel? level, string? searchText, DateTime? from, DateTime? to)
    {
        lock (_lock)
        {
            var query = _entries.AsEnumerable();

            if (level.HasValue)
                query = query.Where(e => e.Level == level.Value);

            if (!string.IsNullOrWhiteSpace(searchText))
                query = query.Where(e => e.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase));

            if (from.HasValue)
                query = query.Where(e => e.Timestamp >= from.Value);

            if (to.HasValue)
                query = query.Where(e => e.Timestamp <= to.Value);

            return query.OrderByDescending(e => e.Timestamp).Take(500).ToList();
        }
    }

    public void ExportToCsv(string filePath)
    {
        lock (_lock)
        {
            var lines = new List<string> { "Timestamp;Level;Source;Message" };
            lines.AddRange(_entries.Select(e =>
                $"{e.Timestamp:yyyy-MM-dd HH:mm:ss};{e.Level};{e.Source};{e.Message.Replace(";", ",")}"));
            File.WriteAllLines(filePath, lines);
        }
    }

    public void ExportToJson(string filePath)
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
    }
}
