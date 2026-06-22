using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackupApp.Interfaces;
using BackupApp.Models;
using BackupApp.Services;
using BackupApp.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;

namespace BackupApp;

public partial class MainForm : Form
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_TEXT_COLOR = 36;
    private static readonly JsonSerializerOptions JsonOptions = JsonHelper.Options;
    private readonly ISettingsService _settingsService;
    private readonly IBackupOrchestrator _orchestrator;
    private readonly IBackupEngine _backupEngine;
    private readonly IScheduleCalculator _scheduleCalculator;
    private readonly ITargetTransferService _transferService;
    private readonly ILogService _log;
    private readonly INotificationService _notificationService;
    private readonly IDashboardService _dashboardService;
    private readonly BackupSchedulerService _scheduler;

    private NotifyIcon? _trayIcon;
    private bool _isClosing;

    public MainForm(
        ISettingsService settingsService,
        IBackupOrchestrator orchestrator,
        IBackupEngine backupEngine,
        IScheduleCalculator scheduleCalculator,
        ITargetTransferService transferService,
        ILogService log,
        INotificationService notificationService,
        IDashboardService dashboardService,
        BackupSchedulerService scheduler)
    {
        _settingsService = settingsService;
        _orchestrator = orchestrator;
        _backupEngine = backupEngine;
        _scheduleCalculator = scheduleCalculator;
        _transferService = transferService;
        _log = log;
        _notificationService = notificationService;
        _dashboardService = dashboardService;
        _scheduler = scheduler;

        InitializeComponent();

        var settings = _settingsService.Load();

        if (settings.App.MinimizeToTray)
            SetupTrayIcon();

        if (!string.IsNullOrEmpty(settings.App.Hotkey))
            SetupHotkey(settings.App.Hotkey);

        this.Load += MainForm_Load;
        this.FormClosing += MainForm_FormClosing;
        this.Resize += MainForm_Resize;
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        try
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                _log.Info($"WebView2 Runtime: {version}", "MainForm");
            }
            catch
            {
                var msg = "Не установлен WebView2 Runtime.\n\n" +
                    "Для работы приложения требуется WebView2 Runtime от Microsoft.\n" +
                    "Скачайте и установите его с официального сайта:\n" +
                    "https://developer.microsoft.com/ru-ru/microsoft-edge/webview2/";
                MessageBox.Show(msg, "Требуется WebView2 Runtime",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _log.Error("WebView2 Runtime не обнаружен", "MainForm");
                Application.Exit();
                return;
            }

            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--allow-file-access-from-files"
            };
            var env = await CoreWebView2Environment.CreateAsync(null, null, envOptions);
            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            webView.CoreWebView2.AddHostObjectToScript("host", new HostObject(this));

            var htmlPath = Path.Combine(Application.StartupPath, "wwwroot", "index.html");
            if (File.Exists(htmlPath))
            {
                webView.CoreWebView2.Navigate($"file:///{htmlPath.Replace("\\", "/")}");
            }
            else
            {
                webView.CoreWebView2.Navigate("about:blank");
                await webView.CoreWebView2.ExecuteScriptAsync(@"
                    document.body.innerHTML = '<div style=""font-family: Arial; padding: 40px;""><h1>Backup App</h1><p>WebView2 загружен.</p></div>';
                ");
            }

            SetTitleBarColor();

            _log.Info("Приложение запущено", "MainForm");
            await _orchestrator.StartSchedulerAsync();

            var settings = _settingsService.Load();
            if (!settings.Sources.Any() && !settings.Targets.Any())
            {
                ShowWizard();
            }
        }
        catch (Exception ex)
        {
            var msg = $"Ошибка инициализации: {ex.Message}\n\n" +
                "Попробуйте переустановить WebView2 Runtime или обратитесь в поддержку.";
            MessageBox.Show(msg, "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _log.Error($"Ошибка инициализации: {ex.Message}", "MainForm");
            Application.Exit();
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebMessageRequest? request = null;
        try
        {
            var json = e.TryGetWebMessageAsString();
            request = JsonSerializer.Deserialize<WebMessageRequest>(json, JsonOptions);
            if (request == null) return;

            object? result = null;
            var settings = _settingsService.Load();

            switch (request.Action)
            {
                case "getDashboard":
                    result = await _dashboardService.GetDataAsync();
                    break;

                case "getSources":
                    result = settings.Sources;
                    break;

                case "addSource":
                    var newSource = JsonSerializer.Deserialize<BackupSource>(
                        JsonSerializer.Serialize(request.Data, JsonOptions), JsonOptions);
                    if (newSource != null)
                    {
                        newSource.Id = Guid.NewGuid();
                        settings.Sources.Add(newSource);
                        _settingsService.Save(settings);
                        _log.Info($"Добавлен источник: {newSource.Name}", "MainForm");
                    }
                    result = settings.Sources;
                    break;

                case "removeSource":
                    if (request.Data != null && request.Data.TryGetValue("id", out var idObj))
                    {
                        var sid = Guid.Parse(idObj?.ToString()!);
                        settings.Sources.RemoveAll(s => s.Id == sid);
                        settings.Schedules.RemoveAll(s => s.SourceId == sid);
                        _settingsService.Save(settings);
                    }
                    result = settings.Sources;
                    break;

                case "updateSource":
                    var updatedSource = JsonSerializer.Deserialize<BackupSource>(
                        JsonSerializer.Serialize(request.Data, JsonOptions), JsonOptions);
                    if (updatedSource != null)
                    {
                        var idx = settings.Sources.FindIndex(s => s.Id == updatedSource.Id);
                        if (idx >= 0)
                        {
                            settings.Sources[idx] = updatedSource;
                            _settingsService.Save(settings);
                        }
                    }
                    result = settings.Sources;
                    break;

                case "calculateSchedule":
                    var path = request.Data?["path"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        result = _scheduleCalculator.CalculateOptimalSchedule(path);
                    }
                    break;

                case "getSchedules":
                    result = settings.Schedules;
                    break;

                case "saveSchedule":
                    var newSchedule = JsonSerializer.Deserialize<Schedule>(
                        JsonSerializer.Serialize(request.Data, JsonOptions), JsonOptions);
                    if (newSchedule != null)
                    {
                        var existing = settings.Schedules.FirstOrDefault(s => s.SourceId == newSchedule.SourceId);
                        if (existing != null)
                        {
                            newSchedule.Id = existing.Id;
                            newSchedule.LastRun = existing.LastRun;
                        }
                        else
                        {
                            newSchedule.Id = Guid.NewGuid();
                        }
                        settings.Schedules.RemoveAll(s => s.SourceId == newSchedule.SourceId);
                        settings.Schedules.Add(newSchedule);
                        _settingsService.Save(settings);
                    }
                    result = settings.Schedules;
                    break;

                case "getTargets":
                    result = settings.Targets;
                    break;

                case "addTarget":
                    var newTarget = JsonSerializer.Deserialize<TargetPC>(
                        JsonSerializer.Serialize(request.Data, JsonOptions), JsonOptions);
                    if (newTarget != null)
                    {
                        newTarget.Id = Guid.NewGuid();
                        settings.Targets.Add(newTarget);
                        _settingsService.Save(settings);
                        _log.Info($"Добавлен целевой ПК: {newTarget.Name}", "MainForm");
                    }
                    result = settings.Targets;
                    break;

                case "removeTarget":
                    if (request.Data?.TryGetValue("id", out var tidObj) == true)
                    {
                        var tid = Guid.Parse(tidObj?.ToString()!);
                        settings.Targets.RemoveAll(t => t.Id == tid);
                        _settingsService.Save(settings);
                    }
                    result = settings.Targets;
                    break;

                case "updateTarget":
                    var updatedTarget = JsonSerializer.Deserialize<TargetPC>(
                        JsonSerializer.Serialize(request.Data, JsonOptions), JsonOptions);
                    if (updatedTarget != null)
                    {
                        var tIdx = settings.Targets.FindIndex(t => t.Id == updatedTarget.Id);
                        if (tIdx >= 0)
                        {
                            settings.Targets[tIdx] = updatedTarget;
                            _settingsService.Save(settings);
                        }
                    }
                    result = settings.Targets;
                    break;

                case "checkTarget":
                    if (request.Data?.TryGetValue("id", out var cidObj) == true)
                    {
                        var cid = Guid.Parse(cidObj?.ToString()!);
                        var target = settings.Targets.FirstOrDefault(t => t.Id == cid);
                        if (target != null)
                        {
                            result = await _transferService.CheckAvailabilityAsync(target);
                        }
                    }
                    break;

                case "startBackup":
                    if (request.Data?.TryGetValue("sourceId", out var sidObj) == true)
                    {
                        var backupSourceId = Guid.Parse(sidObj?.ToString()!);
                        if (!_scheduler.TryStartBackup(backupSourceId))
                        {
                            result = new { error = "Бэкап для этого источника уже выполняется" };
                            break;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var source = _settingsService.Load().Sources.FirstOrDefault(s => s.Id == backupSourceId);
                                SendEventToWeb("backupStarted", new { sourceName = source?.Name ?? "", percentage = 0, phase = "Загрузка..." });
                                var backupProgress = new Progress<BackupProgress>(p =>
                                {
                                    SendEventToWeb("backupProgress", p);
                                });
                                var backupResult = await _orchestrator.RunBackupAsync(backupSourceId, CancellationToken.None, backupProgress);
                                SendEventToWeb("backupCompleted", backupResult);
                            }
                            catch (Exception ex)
                            {
                                SendEventToWeb("backupError", new { sourceId = backupSourceId, error = ex.Message });
                            }
                            finally
                                {
                                    _scheduler.FinishBackup(backupSourceId);
                                }
                        });
                        result = new { message = "Бэкап запущен" };
                    }
                    break;

                case "getLogs":
                    LogLevel? levelFilter = null;
                    if (request.Data?.TryGetValue("level", out var lvl) == true && lvl != null)
                    {
                        var lvlStr = lvl.ToString();
                        if (!string.IsNullOrEmpty(lvlStr))
                            levelFilter = Enum.Parse<LogLevel>(lvlStr, true);
                    }
                    var searchFilter = request.Data?.TryGetValue("search", out var src) == true
                        ? src?.ToString() : null;
                    var rawLogs = _log.GetFiltered(levelFilter, string.IsNullOrEmpty(searchFilter) ? null : searchFilter, null, null);
                    result = rawLogs.Select(l => new {
                        l.Timestamp,
                        Level = l.Level.ToString(),
                        l.Message,
                        l.Source
                    }).ToList();
                    break;

                case "exportLogs":
                    var format = request.Data?["format"]?.ToString() ?? "csv";
                    var exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"backup_logs_{DateTime.Now:yyyyMMdd_HHmmss}.{format}");
                    if (format == "csv")
                        _log.ExportToCsv(exportPath);
                    else
                        _log.ExportToJson(exportPath);
                    result = new { path = exportPath };
                    break;

                case "getSettings":
                    result = settings;
                    break;

                case "saveSettings":
                    var newSettings = JsonSerializer.Deserialize<AppSettings>(
                        JsonSerializer.Serialize(request.Data, JsonOptions), JsonOptions);
                    if (newSettings != null)
                    {
                        _settingsService.Save(newSettings);
                    }
                    result = _settingsService.Load();
                    break;

                case "exportConfig":
                    var exportConfigPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"backup_config_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                    _settingsService.Export(exportConfigPath);
                    result = new { path = exportConfigPath };
                    break;

                case "importConfig":
                    using (var dialog = new OpenFileDialog { Filter = "JSON files (*.json)|*.json" })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            _settingsService.Import(dialog.FileName);
                            result = _settingsService.Load();
                        }
                    }
                    break;

                case "pickFolder":
                    using (var folderDialog = new FolderBrowserDialog())
                    {
                        if (folderDialog.ShowDialog(this) == DialogResult.OK)
                        {
                            result = new { path = folderDialog.SelectedPath };
                        }
                    }
                    break;

                case "getAppVersion":
                    result = new { version = "1.0.0", framework = ".NET 8" };
                    break;

                case "getPendingTransfers":
                    result = _orchestrator.GetPendingTransfers();
                    break;

                case "processQueue":
                    await _orchestrator.ProcessPendingTransfersAsync();
                    result = new { message = "Очередь обработана" };
                    break;

                case "verifyIntegrity":
                    if (request.Data?.TryGetValue("path", out var intPath) == true)
                    {
                        var filePath = intPath?.ToString();
                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            var hash = Utils.CryptoHelper.ComputeSha256(filePath);
                            result = new { hash, file = filePath };
                        }
                        else
                        {
                            result = new { error = "Файл не найден" };
                        }
                    }
                    break;

                case "estimateSize":
                    if (request.Data?.TryGetValue("path", out var estPath) == true)
                    {
                        var estSourcePath = estPath?.ToString() ?? "";
                        var patterns = new List<string>();
                        if (request.Data.TryGetValue("patterns", out var patObj) && patObj is System.Text.Json.JsonElement patEl)
                        {
                            patterns = patEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        }

                        if (!string.IsNullOrEmpty(estSourcePath) && Directory.Exists(estSourcePath))
                        {
                            var allFiles = Services.BackupEngine.GetEstimatedSize(estSourcePath, patterns);
                            result = allFiles;
                        }
                    }
                    break;

                case "findLocalBackups":
                    if (request.Data?.TryGetValue("path", out var lbPath) == true && request.Data?.TryGetValue("pattern", out var lbPat) == true)
                    {
                        var dir = lbPath?.ToString() ?? "";
                        var pat = lbPat?.ToString() ?? "*.zip";
                        if (Directory.Exists(dir))
                        {
                            result = Directory.GetFiles(dir, pat).OrderByDescending(f => new FileInfo(f).LastWriteTime).ToList();
                        }
                        else
                        {
                            result = Array.Empty<string>();
                        }
                    }
                    break;
            }

            var response = JsonSerializer.Serialize(new
            {
                requestId = request.RequestId,
                result
            }, JsonOptions);
            webView.CoreWebView2.PostWebMessageAsString(response);
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка обработки запроса: {ex.Message}", "MainForm");
            try
            {
                var errResponse = JsonSerializer.Serialize(new
                {
                    requestId = request?.RequestId ?? 0,
                    result = new { error = ex.Message }
                }, JsonOptions);
                webView?.CoreWebView2?.PostWebMessageAsString(errResponse);
            }
            catch { }
        }
    }

    public void SendEventToWeb(string eventName, object data)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "event",
                name = eventName,
                data
            }, JsonOptions);
            webView?.CoreWebView2?.PostWebMessageAsString(payload);
        }
        catch { }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "Backup App",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Открыть", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; });
        contextMenu.Items.Add("Бэкап сейчас", null, async (_, _) =>
        {
            var settings = _settingsService.Load();
            var enabledSources = settings.Sources.Where(s => s.IsEnabled).ToList();
            foreach (var source in enabledSources)
            {
                await _orchestrator.RunBackupAsync(source.Id);
            }
        });
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Выход", null, (_, _) =>
        {
            _isClosing = true;
            Application.Exit();
        });

        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; };
    }

    private void SetupHotkey(string hotkeyString)
    {
        var match = Regex.Match(hotkeyString, @"(?:(Ctrl|Alt|Shift)\+)?(?:(Ctrl|Alt|Shift)\+)?(\w)");
        if (!match.Success) return;

        var key = Enum.Parse<Keys>(match.Groups[3].Value, true);
        if (match.Groups[1].Value == "Ctrl") key |= Keys.Control;
        if (match.Groups[2].Value == "Ctrl") key |= Keys.Control;
        if (match.Groups[1].Value == "Alt") key |= Keys.Alt;
        if (match.Groups[2].Value == "Alt") key |= Keys.Alt;
        if (match.Groups[1].Value == "Shift") key |= Keys.Shift;
        if (match.Groups[2].Value == "Shift") key |= Keys.Shift;

        this.KeyPreview = true;
        this.KeyDown += (_, args) =>
        {
            if (args.KeyData == key)
            {
                var settings = _settingsService.Load();
                var enabledSources = settings.Sources.Where(s => s.IsEnabled).ToList();
                foreach (var source in enabledSources)
                {
                    _ = _orchestrator.RunBackupAsync(source.Id);
                }
                args.Handled = true;
            }
        };
    }

    private async void ShowWizard()
    {
        var result = MessageBox.Show(
            "Добро пожаловать в Backup App!\n\nХотите выполнить первоначальную настройку?",
            "Мастер настройки",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            var settings = _settingsService.Load();

            using var folderDialog = new FolderBrowserDialog
            {
                Description = "Выберите папку для резервного копирования"
            };

            if (folderDialog.ShowDialog(this) == DialogResult.OK)
            {
                var source = new BackupSource
                {
                    Id = Guid.NewGuid(),
                    Name = new DirectoryInfo(folderDialog.SelectedPath).Name,
                    Path = folderDialog.SelectedPath,
                    IsEnabled = true
                };

                var schedule = _scheduleCalculator.CalculateOptimalSchedule(source.Path);
                schedule.Id = Guid.NewGuid();
                schedule.SourceId = source.Id;

                settings.Sources.Add(source);
                settings.Schedules.Add(schedule);
                _settingsService.Save(settings);

                _log.Info($"Добавлен источник через мастер: {source.Name}", "Wizard");

                var addTarget = MessageBox.Show(
                    "Источник добавлен. Добавить целевой ПК для копирования?",
                    "Мастер настройки",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (addTarget == DialogResult.Yes)
                {
                    SendEventToWeb("showAddTarget", new { });
                }
            }
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isClosing && _trayIcon != null)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(1000, "Backup App", "Приложение свёрнуто в трей", ToolTipIcon.Info);
            return;
        }

        _orchestrator.StopScheduler();
        _trayIcon?.Dispose();
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized && _trayIcon != null)
        {
            Hide();
        }
    }

    private void SetTitleBarColor()
    {
        if (Environment.OSVersion.Version.Major < 10) return;
        try
        {
            var bgColor = 0x00D47800;
            var textColor = 0x00FFFFFF;
            DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref bgColor, 4);
            DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref textColor, 4);
        }
        catch { }
    }
}

[ComVisible(true)]
public class HostObject
{
    private readonly MainForm _form;
    public HostObject(MainForm form) => _form = form;

    public void ShowFolderPicker()
    {
        using var dialog = new FolderBrowserDialog();
        var result = dialog.ShowDialog(_form);
        _form.SendEventToWeb("folderSelected", new
        {
            path = result == DialogResult.OK ? dialog.SelectedPath : null
        });
    }
}
