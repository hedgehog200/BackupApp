using BackupApp.Models;

namespace BackupApp.Interfaces;

public interface ILogService
{
    void Info(string message, string? source = null);
    void Warning(string message, string? source = null);
    void Error(string message, string? source = null);
    List<LogEntry> GetRecent(int count = 100);
    List<LogEntry> GetFiltered(LogLevel? level, string? searchText, DateTime? from, DateTime? to);
    void ExportToCsv(string filePath);
    void ExportToJson(string filePath);
}
