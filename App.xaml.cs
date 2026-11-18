using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    const string InstanceName = "DiscordIsland.SingleInstance";
    const string LogFileName = "DiscordIsland.log";
    static readonly TimeSpan PredecessorExitTimeout = TimeSpan.FromSeconds(10);

    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        string currentExePath = Environment.ProcessPath ?? "";
        Version currentVersion = Updater.CurrentVersion;

        // 1. Проверка уже запущенных процессов DiscordIsland
        Process currentProcess = Process.GetCurrentProcess();
        Process[] others = Process.GetProcessesByName(currentProcess.ProcessName)
            .Where(p => p.Id != currentProcess.Id)
            .ToArray();

        if (others.Length > 0 && !e.Args.Contains(Updater.RestartedFlag))
        {
            foreach (Process other in others)
            {
                string? otherPath = null;
                try { otherPath = other.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(otherPath)) continue;

                // Запуск того же самого файла: предотвращаем запуск второго экземпляра
                if (string.Equals(otherPath, currentExePath, StringComparison.OrdinalIgnoreCase))
                {
                    Shutdown();
                    return;
                }

                // Запущен другой файл DiscordIsland.exe на диске
                Version otherVersion = GetFileVersion(otherPath);

                if (currentVersion >= otherVersion)
                {
                    var replaceChoice = MessageBox.Show(
                        $"Обнаружена уже запущенная копия Discord Island:\n" +
                        $"• Версия: v{otherVersion}\n" +
                        $"• Путь: {otherPath}\n\n" +
                        $"Вы запустили версию v{currentVersion} из:\n" +
                        $"• {currentExePath}\n\n" +
                        $"Завершить предыдущую копию и использовать новую?",
                        "Discord Island — Выбор версии",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (replaceChoice == MessageBoxResult.Yes)
                    {
                        try
                        {
                            other.Kill();
                            other.WaitForExit(4000);
                        }
                        catch (Exception ex)
                        {
                            Log(ex);
                        }

                        // Если автозагрузка ссылалась на старый файл, обновляем её
                        string? registered = Autostart.RegisteredPath;
                        if (!string.IsNullOrEmpty(registered) && string.Equals(registered, otherPath, StringComparison.OrdinalIgnoreCase))
                        {
                            Autostart.Set(true);
                        }

                        // Предлагаем удалить старый файл
                        if (File.Exists(otherPath))
                        {
                            var delChoice = MessageBox.Show(
                                $"Удалить старый файл программы, чтобы не оставлять дубликатов?\n\n{otherPath}",
                                "Discord Island — Удаление старой версии",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                            if (delChoice == MessageBoxResult.Yes)
                            {
                                try { File.Delete(otherPath); }
                                catch (Exception ex) { Log(ex); }
                            }
                        }
                    }
                    else
                    {
                        Shutdown();
                        return;
                    }
                }
                else
                {
                    // Запущенный экземпляр новее текущего файла
                    var delChoice = MessageBox.Show(
                        $"Уже запущена более новая версия Discord Island (v{otherVersion}) из:\n" +
                        $"{otherPath}\n\n" +
                        $"Текущий файл имеет устаревшую версию (v{currentVersion}).\n\n" +
                        $"Удалить этот устаревший файл?",
                        "Discord Island — Устаревшая версия",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (delChoice == MessageBoxResult.Yes)
                    {
                        DeleteOnExit(currentExePath);
                    }
                    Shutdown();
                    return;
                }
            }
        }

        // 2. Блокировка единственного экземпляра через Mutex
        _mutex = new Mutex(true, InstanceName, out bool isOnlyInstance);
        if (!isOnlyInstance && e.Args.Contains(Updater.RestartedFlag)) isOnlyInstance = WaitForPredecessor(_mutex);
        if (!isOnlyInstance)
        {
            Shutdown();
            return;
        }

        // 3. Проверка автозагрузки Windows на случай, если старый файл прописан там
        CheckAutostartRedirection(currentExePath, currentVersion);

        Updater.CleanUp();

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        new MainWindow().Show();
    }

    private static void CheckAutostartRedirection(string currentExePath, Version currentVersion)
    {
        string? registered = Autostart.RegisteredPath;
        if (string.IsNullOrEmpty(registered) || string.Equals(registered, currentExePath, StringComparison.OrdinalIgnoreCase))
            return;

        // В автозагрузке прописан другой файл
        if (!File.Exists(registered))
        {
            Autostart.Set(true);
            return;
        }

        Version oldVersion = GetFileVersion(registered);
        if (currentVersion >= oldVersion)
        {
            var updateChoice = MessageBox.Show(
                $"В автозагрузке Windows указана другая копия программы:\n" +
                $"• Версия: v{oldVersion}\n" +
                $"• Расположение: {registered}\n\n" +
                $"Вы используете версию v{currentVersion} из:\n" +
                $"• {currentExePath}\n\n" +
                $"Обновить автозагрузку на текущий файл?",
                "Discord Island — Автозагрузка",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (updateChoice == MessageBoxResult.Yes)
            {
                Autostart.Set(true);

                var delChoice = MessageBox.Show(
                    $"Удалить старый файл из автозагрузки, чтобы не оставлять дубликатов?\n\n{registered}",
                    "Discord Island — Удаление старой копии",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (delChoice == MessageBoxResult.Yes)
                {
                    try { File.Delete(registered); }
                    catch (Exception ex) { Log(ex); }
                }
            }
        }
    }

    private static Version GetFileVersion(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var fvi = FileVersionInfo.GetVersionInfo(filePath);
                if (!string.IsNullOrEmpty(fvi.FileVersion) && Version.TryParse(fvi.FileVersion, out var v))
                {
                    return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
                }
            }
        }
        catch { }
        return new Version(0, 0, 0);
    }

    private static void DeleteOnExit(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak > NUL & del /f /q \"{path}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), LogFileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    static bool WaitForPredecessor(Mutex mutex)
    {
        try { return mutex.WaitOne(PredecessorExitTimeout); }
        catch (AbandonedMutexException) { return true; }
    }
}
