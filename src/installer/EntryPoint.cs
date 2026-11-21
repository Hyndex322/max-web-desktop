using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MaxSetup
{
    internal static class EntryPoint
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetupOptions options = SetupOptions.Parse(args);
            Logger.Init();
            Logger.Write("Command line: " + string.Join(" ", args));

            try
            {
                if (!string.IsNullOrEmpty(options.CleanupDir))
                {
                    int code = InstallerEngine.CleanupDirectory(options.CleanupDir);
                    InstallerEngine.ScheduleSelfDelete();
                    return code;
                }

                if (options.Uninstall)
                {
                    string dir = options.Dir;
                    if (string.IsNullOrEmpty(dir))
                        dir = InstallerEngine.GetInstalledDir();

                    if (string.IsNullOrEmpty(dir))
                    {
                        if (!options.Silent)
                        {
                            MessageBox.Show(
                                "Не удалось найти установленный MAX.",
                                "MAX",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        return 3;
                    }

                    if (options.Silent)
                    {
                        InstallerEngine.Uninstall(dir, options.KeepData, null);
                        return 0;
                    }

                    options.Dir = dir;
                    Application.Run(new InstallerForm(options));
                    return 0;
                }

                if (options.Silent)
                {
                    string dir;
                    try
                    {
                        dir = InstallerEngine.NormalizeInstallDir(
                            string.IsNullOrEmpty(options.Dir)
                                ? InstallerEngine.DefaultInstallDir
                                : options.Dir);
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Invalid silent install path: " + ex.Message);
                        return 2;
                    }

                    InstallerEngine.Install(dir, !options.NoDesktop, null);
                    return 0;
                }

                Application.Run(new InstallerForm(options));
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Write("FATAL: " + ex);
                if (!options.Silent)
                {
                    MessageBox.Show(
                        "Не удалось выполнить операцию.\n\n" + ex.Message +
                        "\n\nЖурнал: " + Logger.FilePath,
                        "MAX",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return 1;
            }
        }
    }

    internal sealed class InstallProgress
    {
        public readonly int Percent;
        public readonly string Text;

        public InstallProgress(int percent, string text)
        {
            Percent = percent;
            Text = text;
        }
    }

    internal static class InstallerEngine
    {
        public const string AppExeName = "Max.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string ProductName = "MAX Messenger";
        public const string Version = "1.0.0";
        public const string RegistryUninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MAXMessenger";
        public const string RegistryAppKey = @"Software\MAXMessenger";

        private const string PayloadResource = "payload.zip";
        private const string WebView2Resource = "webview2setup.exe";

        public static string DefaultInstallDir
        {
            get
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) root = @"C:\";
                return Path.Combine(root, "Max");
            }
        }

        public static bool IsAppRunning()
        {
            try
            {
                return Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(AppExeName)).Length > 0;
            }
            catch { return false; }
        }

        public static string NormalizeInstallDir(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Укажите папку для установки.");

            string path = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
            path = path.TrimEnd('\\');

            if (path.Length == 2 && path[1] == ':')
                path += "\\Max";
            else if (path.Length == 0)
                throw new ArgumentException("Укажите папку для установки.");

            path = Path.GetFullPath(path);

            string root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(root, "Max");
            }

            return path.TrimEnd('\\');
        }

        public static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static bool RelaunchElevated(string arguments)
        {
            try
            {
                var info = new ProcessStartInfo(Application.ExecutablePath, arguments);
                info.UseShellExecute = true;
                info.Verb = "runas";
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("Elevation cancelled or failed: " + ex.Message);
                return false;
            }
        }

        public static void Launch(string dir)
        {
            string exe = Path.Combine(dir, AppExeName);
            if (!File.Exists(exe)) return;

            try
            {
                var info = new ProcessStartInfo(exe);
                info.WorkingDirectory = dir;
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to launch MAX: " + ex.Message);
            }
        }

        public static bool IsWebView2Installed()
        {
            const string clientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            string[] subKeys =
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + clientId,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + clientId
            };

            RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        {
                            foreach (string subKey in subKeys)
                            {
                                using (RegistryKey key = baseKey.OpenSubKey(subKey))
                                {
                                    if (key == null) continue;
                                    string pv = key.GetValue("pv") as string;
                                    if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0")
                                        return true;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return false;
        }

        public static bool EnsureWebView2()
        {
            if (IsWebView2Installed()) return true;

            Logger.Write("WebView2 runtime not found, installing bootstrapper...");
            string temp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");

            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(WebView2Resource))
                {
                    if (stream == null)
                    {
                        Logger.Write("Embedded WebView2 bootstrapper is missing.");
                        return false;
                    }

                    using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
                        stream.CopyTo(file);
                }

                var info = new ProcessStartInfo(temp, "/silent /install");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;

                using (Process process = Process.Start(info))
                {
                    if (process != null)
                    {
                        process.WaitForExit(600000);
                        Logger.Write("WebView2 bootstrapper exit code: " + process.ExitCode);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("WebView2 bootstrap failed: " + ex.Message);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }

            for (int i = 0; i < 10; i++)
            {
                if (IsWebView2Installed()) return true;
                Thread.Sleep(1000);
            }

            return IsWebView2Installed();
        }

        public static void Install(string dir, bool desktopShortcut, IProgress<InstallProgress> progress)
        {
            dir = NormalizeInstallDir(dir);

            Report(progress, 0, "Подготовка...");
            KillRunningApp();

            Report(progress, 3, "Проверка папки установки...");
            EnsureWritable(dir);

            if (!IsWebView2Installed())
            {
                Report(progress, 5, "Установка компонента Microsoft Edge WebView2...");
                if (!EnsureWebView2())
                {
                    Report(progress, 18,
                        "WebView2 не установлен. MAX попросит установить его при первом запуске.");
                }
            }

            Report(progress, 22, "Распаковка файлов...");
            ExtractPayload(dir, progress, 22, 68);

            Report(progress, 72, "Подготовка удаления...");
            CopyUninstaller(dir);

            Report(progress, 78, "Создание ярлыков...");
            if (!CreateShortcuts(dir, desktopShortcut))
                Report(progress, 82, "Не удалось создать ярлыки. Файлы программы установлены.");

            Report(progress, 88, "Регистрация программы...");
            WriteRegistry(dir);

            Report(progress, 100, "Установка завершена");
        }

        public static void Uninstall(
            string dir,
            bool keepUserData,
            IProgress<InstallProgress> progress)
        {
            if (string.IsNullOrEmpty(dir))
                throw new ArgumentException("Не найдена папка установки.");

            Report(progress, 0, "Удаление MAX...");
            KillRunningApp();

            Report(progress, 10, "Удаление ярлыков...");
            DeleteShortcuts();

            Report(progress, 20, "Удаление записей реестра...");
            DeleteRegistry();

            bool selfInside = IsSelfInsideDirectory(dir);

            Report(progress, 30, "Удаление файлов...");
            if (Directory.Exists(dir))
                DeleteApplicationFiles(dir, selfInside);

            if (!keepUserData)
            {
                Report(progress, 75, "Удаление сохранённой сессии...");
                DeleteUserData();
            }

            Report(progress, 90, "Завершение...");
            if (selfInside)
                ScheduleDirectoryCleanup(dir);
            else
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    Logger.Write("Direct directory removal failed: " + ex.Message);
                    ScheduleDirectoryCleanup(dir);
                }
            }

            Report(progress, 100, "MAX удалён");
        }

        public static string GetInstalledDir()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryAppKey))
                {
                    if (key != null)
                    {
                        string dir = key.GetValue("InstallDir") as string;
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            return dir;
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryUninstallKey))
                {
                    if (key != null)
                    {
                        string dir = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            return dir;
                    }
                }
            }
            catch { }

            string baseDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (!string.IsNullOrEmpty(baseDir) &&
                File.Exists(Path.Combine(baseDir, AppExeName)))
                return baseDir;

            return null;
        }

        public static void KillRunningApp()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(AppExeName)))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                        Logger.Write("Closed running MAX process " + process.Id);
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Failed to close process " + process.Id + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to enumerate MAX processes: " + ex.Message);
            }
        }

        public static bool CreateShortcuts(string dir, bool desktopShortcut)
        {
            string exe = Path.Combine(dir, AppExeName);
            if (!File.Exists(exe))
                throw new FileNotFoundException("Не найден файл приложения.", exe);

            bool ok = true;

            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "MAX.lnk");
            try
            {
                ShortcutHelper.Create(startMenu, exe, dir, "MAX Messenger");
            }
            catch (Exception ex)
            {
                ok = false;
                Logger.Write("Start menu shortcut failed: " + ex.Message);
            }

            if (desktopShortcut)
            {
                string desktop = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "MAX.lnk");
                try
                {
                    ShortcutHelper.Create(desktop, exe, dir, "MAX Messenger");
                }
                catch (Exception ex)
                {
                    ok = false;
                    Logger.Write("Desktop shortcut failed: " + ex.Message);
                }
            }

            return ok;
        }

        public static void DeleteShortcuts()
        {
            string[] paths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MAX.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MAX.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MAX Messenger.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MAX Messenger.lnk")
            };

            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to delete shortcut " + path + ": " + ex.Message);
                }
            }
        }

        public static void WriteRegistry(string dir)
        {
            string exe = Path.Combine(dir, AppExeName);
            string uninstaller = Path.Combine(dir, UninstallerName);

            int sizeKb = 1;
            try
            {
                long size = 0;
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
                sizeKb = (int)Math.Max(1, size / 1024);
            }
            catch { }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryUninstallKey))
            {
                if (key == null) throw new IOException("Не удалось создать запись реестра.");
                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "MAX");
                key.SetValue("InstallLocation", dir);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryAppKey))
            {
                if (key != null)
                {
                    key.SetValue("InstallDir", dir);
                    key.SetValue("Version", Version);
                }
            }
        }

        public static void DeleteRegistry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryUninstallKey, false);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to delete uninstall registry key: " + ex.Message);
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryAppKey, false);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to delete app registry key: " + ex.Message);
            }
        }

        public static int CleanupDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return 1;

            for (int attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return 0;
                    Directory.Delete(dir, true);
                    Logger.Write("Cleanup removed directory " + dir);
                    return 0;
                }
                catch (Exception ex)
                {
                    if (attempt == 0 || attempt % 10 == 0)
                        Logger.Write("Cleanup retry: " + ex.Message);
                    Thread.Sleep(500);
                }
            }

            Logger.Write("Cleanup failed for " + dir);
            return 1;
        }

        public static void ScheduleSelfDelete()
        {
            try
            {
                string self = Application.ExecutablePath;
                var info = new ProcessStartInfo(
                    "cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + self + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to schedule self delete: " + ex.Message);
            }
        }

        private static void ScheduleDirectoryCleanup(string dir)
        {
            try
            {
                string temp = Path.Combine(
                    Path.GetTempPath(),
                    "MaxCleanup_" + Guid.NewGuid().ToString("N") + ".exe");

                File.Copy(Application.ExecutablePath, temp, true);

                var info = new ProcessStartInfo(temp, "/cleanup \"" + dir + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                Process.Start(info);

                Logger.Write("Scheduled directory cleanup: " + dir);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to schedule directory cleanup: " + ex.Message);
            }
        }

        private static void DeleteApplicationFiles(string dir, bool keepSelf)
        {
            string self = Path.GetFullPath(Application.ExecutablePath);

            foreach (string subDir in Directory.GetDirectories(dir))
            {
                try
                {
                    File.SetAttributes(subDir, FileAttributes.Normal);
                    Directory.Delete(subDir, true);
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to delete directory " + subDir + ": " + ex.Message);
                }
            }

            foreach (string file in Directory.GetFiles(dir))
            {
                try
                {
                    if (keepSelf &&
                        string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
                        continue;

                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to delete file " + file + ": " + ex.Message);
                }
            }
        }

        private static bool IsSelfInsideDirectory(string dir)
        {
            try
            {
                string fullDir = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                string self = Path.GetFullPath(Application.ExecutablePath);
                return self.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void DeleteUserData()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MAX Messenger");

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!Directory.Exists(root)) return;
                    Directory.Delete(root, true);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 9)
                        Logger.Write("Failed to delete user data: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        private static void EnsureWritable(string dir)
        {
            Directory.CreateDirectory(dir);

            string probe = Path.Combine(dir, ".max-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
            {
                file.WriteByte(1);
            }
            File.Delete(probe);
        }

        private static void ExtractPayload(
            string dir,
            IProgress<InstallProgress> progress,
            int fromPercent,
            int toPercent)
        {
            using (Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(PayloadResource))
            {
                if (stream == null)
                    throw new InvalidOperationException("В установщике отсутствуют файлы приложения.");

                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, archive.Entries.Count);
                    int done = 0;
                    string normalizedDir = Path.GetFullPath(dir);
                    string prefix = normalizedDir.TrimEnd('\\') + "\\";

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));

                        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(target.TrimEnd('\\'), normalizedDir, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("Повреждён установочный архив.");
                        }

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                        }
                        else
                        {
                            string parent = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                            if (File.Exists(target))
                                File.SetAttributes(target, FileAttributes.Normal);

                            entry.ExtractToFile(target, true);
                        }

                        done++;
                        int percent = fromPercent +
                            (int)((toPercent - fromPercent) * (double)done / total);
                        Report(progress, percent, "Распаковка файлов... " + done + "/" + total);
                    }
                }
            }
        }

        private static void CopyUninstaller(string dir)
        {
            string source = Path.GetFullPath(Application.ExecutablePath);
            string target = Path.GetFullPath(Path.Combine(dir, UninstallerName));

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return;

            File.Copy(source, target, true);
        }

        private static void Report(IProgress<InstallProgress> progress, int percent, string text)
        {
            Logger.Write(percent + "% " + text);
            if (progress != null)
                progress.Report(new InstallProgress(percent, text));
        }
    }
}
