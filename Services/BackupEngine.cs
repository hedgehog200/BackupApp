using System.IO.Compression;
using BackupApp.Interfaces;
using BackupApp.Models;
using BackupApp.Utils;

namespace BackupApp.Services;

public class BackupEngine : IBackupEngine
{
    private readonly ILogService _log;
    private readonly ISettingsService _settingsService;

    public BackupEngine(ILogService log, ISettingsService settingsService)
    {
        _log = log;
        _settingsService = settingsService;
    }

    public async Task<BackupResult> CreateBackupAsync(
        BackupSource source,
        string outputPath,
        IProgress<BackupProgress> progress,
        CancellationToken ct)
    {
        var result = new BackupResult
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            StartedAt = DateTime.Now,
            Status = BackupStatus.InProgress
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "BackupAtomic", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            if (!Directory.Exists(source.Path))
            {
                throw new DirectoryNotFoundException($"Источник не найден: {source.Path}");
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var archiveBaseName = $"Backup_{source.Name}_{timestamp}";
            var tempArchivePath = Path.Combine(tempDir, $"{archiveBaseName}.zip");
            var files = GetFilesWithExclusions(source.Path, source.ExcludePatterns);

            if (files.Count == 0)
            {
                _log.Warning($"Нет файлов для архивации: {source.Name}", "BackupEngine");
                result.Status = BackupStatus.Completed;
                result.CompletedAt = DateTime.Now;
                return result;
            }

            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            int totalFiles = files.Count;

            _log.Info($"Архивация: {source.Name} — {totalFiles} файлов, {FormatSize(totalBytes)}", "BackupEngine");
            progress?.Report(new BackupProgress
            {
                SourceName = source.Name,
                Phase = $"Оценка: {totalFiles} файлов, {FormatSize(totalBytes)}",
                TotalBytes = totalBytes
            });

            var settings = _settingsService.Load();
            await Task.Run(() =>
            {
                using var zipStream = new FileStream(tempArchivePath, FileMode.Create);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

                long processedBytes = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var file = files[i];
                    var relativePath = Path.GetRelativePath(source.Path, file);
                    var fileInfo = new FileInfo(file);
                    var level = source.CompressionLevel <= 2 ? (CompressionLevel)source.CompressionLevel : CompressionLevel.Optimal;
                    var entry = archive.CreateEntry(relativePath, level);

                    using var entryStream = entry.Open();
                    using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read);
                    fileStream.CopyTo(entryStream);

                    processedBytes += fileInfo.Length;

                    progress?.Report(new BackupProgress
                    {
                        SourceName = source.Name,
                        Percentage = (int)((i + 1) * 100.0 / totalFiles),
                        BytesProcessed = processedBytes,
                        TotalBytes = totalBytes,
                        CurrentFile = relativePath,
                        Phase = $"Архивация ({i + 1}/{totalFiles})"
                    });
                }
            }, ct);

            var finalPath = Path.Combine(outputPath, $"{archiveBaseName}.zip");

            Directory.CreateDirectory(outputPath);

            if (File.Exists(finalPath))
                File.Delete(finalPath);

            File.Move(tempArchivePath, finalPath);

            result.ArchivePath = finalPath;
            var fileInfoResult = new FileInfo(finalPath);
            result.ArchiveSize = fileInfoResult.Length;

            if (settings.LocalStorage.Enabled)
            {
                var localPath = settings.LocalStorage.Path;
                Directory.CreateDirectory(localPath);
                var localCopy = Path.Combine(localPath, $"{archiveBaseName}.zip");
                File.Copy(finalPath, localCopy, true);
                _log.Info($"Локальная копия: {localCopy}", "BackupEngine");
            }

            if (!string.IsNullOrEmpty(source.Password))
            {
                var encPath = Path.Combine(outputPath, $"{archiveBaseName}.enc.zip");
                CryptoHelper.EncryptFileAes(finalPath, encPath, source.Password);
                File.Delete(finalPath);
                File.Move(encPath, finalPath);
                _log.Info($"Архив зашифрован AES-256 (пароль источника)", "BackupEngine");
            }

            result.Status = BackupStatus.Completed;
            result.CompletedAt = DateTime.Now;

            _log.Info($"Архивация завершена: {source.Name} — {FormatSize(result.ArchiveSize)}", "BackupEngine");
        }
        catch (OperationCanceledException)
        {
            result.Status = BackupStatus.Failed;
            result.ErrorMessage = "Отменено пользователем";
            _log.Warning($"Архивация отменена: {source.Name}", "BackupEngine");
        }
        catch (Exception ex)
        {
            result.Status = BackupStatus.Failed;
            result.ErrorMessage = ex.Message;
            _log.Error($"Ошибка архивации {source.Name}: {ex.Message}", "BackupEngine");

            if (File.Exists(result.ArchivePath))
            {
                try { File.Delete(result.ArchivePath); } catch { }
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        return result;
    }

    private static List<string> GetFilesWithExclusions(string sourcePath, List<string> excludePatterns)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            bool excluded = false;

            foreach (var pattern in excludePatterns)
            {
                if (pattern.EndsWith("/") || pattern.EndsWith("\\"))
                {
                    var dirPattern = pattern.TrimEnd('/', '\\');
                    if (relative.Contains(dirPattern + Path.DirectorySeparatorChar) ||
                        relative.Contains(dirPattern + Path.AltDirectorySeparatorChar))
                    {
                        excluded = true;
                        break;
                    }
                }
                else if (pattern.StartsWith("*"))
                {
                    if (relative.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase))
                    {
                        excluded = true;
                        break;
                    }
                }
                else if (relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                         relative.StartsWith(pattern + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                         relative.StartsWith(pattern + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    excluded = true;
                    break;
                }
            }

            if (!excluded) files.Add(file);
        }
        return files;
    }

    public static object GetEstimatedSize(string sourcePath, List<string> excludePatterns)
    {
        try
        {
            var files = GetFilesWithExclusions(sourcePath, excludePatterns);
            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            return new { fileCount = files.Count, totalSize = totalBytes, totalSizeFormatted = FormatSize(totalBytes) };
        }
        catch (Exception ex)
        {
            return new { fileCount = 0, totalSize = 0L, totalSizeFormatted = "0 B", error = ex.Message };
        }
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
