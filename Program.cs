using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using BackupApp.Interfaces;
using BackupApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BackupApp;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Contains("--scheduler"))
        {
            RunScheduler();
            return;
        }

        if (args.Contains("--remove-scheduler"))
        {
            RemoveSchedulerTask();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var services = new ServiceCollection();
        ConfigureServices(services);
        services.AddTransient<MainForm>();
        var sp = services.BuildServiceProvider();

        var log = sp.GetRequiredService<ILogService>();
        EnsureSchedulerTask(log);

        var scheduler = sp.GetRequiredService<BackupSchedulerService>();
        scheduler.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        Application.Run(sp.GetRequiredService<MainForm>());

        scheduler.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    static void RunScheduler()
    {
        using var mutex = new Mutex(true, "BackupAppSchedulerInstance", out var first);
        if (!first) return;

        var services = new ServiceCollection();
        ConfigureServices(services);
        var sp = services.BuildServiceProvider();
        var log = sp.GetRequiredService<ILogService>();

        log.Info("Фоновый планировщик запущен", "Scheduler");

        var scheduler = sp.GetRequiredService<BackupSchedulerService>();
        scheduler.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        try { Thread.Sleep(Timeout.Infinite); }
        catch (ThreadInterruptedException) { }

        scheduler.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        log.Info("Фоновый планировщик остановлен", "Scheduler");
    }

    static void EnsureSchedulerTask(ILogService log)
    {
        try
        {
            var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

            if (!isAdmin)
            {
                log.Info("Пропуск создания задачи планировщика — требуются права администратора. " +
                    "Запустите программу от имени администратора или создайте задачу вручную через: " +
                    $"schtasks /create /tn \"BackupAppScheduler\" /tr \"\\\"{Application.ExecutablePath}\\\" --scheduler\" /sc onlogon /f",
                    "Program");
                return;
            }

            var exe = Application.ExecutablePath;
            const string taskName = "BackupAppScheduler";

            Process.Start("schtasks", $"/delete /tn \"{taskName}\" /f")?.WaitForExit(3000);

            var psi = new ProcessStartInfo("schtasks",
                $"/create /tn \"{taskName}\" /tr \"\\\"{exe}\\\" --scheduler\" /sc onlogon /f")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.GetEncoding(
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            if (proc?.ExitCode == 0)
            {
                log.Info("Задача планировщика создана: " + taskName, "Program");
            }
            else
            {
                var stderr = proc?.StandardError.ReadToEnd() ?? "";
                log.Warning($"Не удалось создать задачу планировщика (код: {proc?.ExitCode}): {stderr.Trim()}", "Program");
            }
        }
        catch (Exception ex)
        {
            log.Warning("Ошибка создания задачи: " + ex.Message, "Program");
        }
    }

    static void RemoveSchedulerTask()
    {
        try
        {
            Process.Start("schtasks", "/delete /tn \"BackupAppScheduler\" /f")?.WaitForExit(3000);
        }
        catch { }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IScheduleCalculator, SmartScheduleCalculator>();
        services.AddSingleton<IRetentionManager, RetentionManager>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddTransient<IBackupEngine, BackupEngine>();
        services.AddTransient<ITargetTransferService, TargetTransferService>();
        services.AddTransient<IBackupOrchestrator, BackupOrchestrator>();
        services.AddSingleton<BackupSchedulerService>();
    }
}
