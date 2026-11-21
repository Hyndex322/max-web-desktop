using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MaxSetup
{
    internal sealed class InstallResult
    {
        public string InstallDir;
        public bool Updated;
        public bool WebView2Available;
        public bool WebView2Attempted;
        public string Warning;
    }

    internal sealed class UninstallResult
    {
        public string InstallDir;
        public bool FolderPreserved;
        public bool CleanupScheduled;
        public bool UserDataDeleted;
        public bool UserDataPreserved;
    }

    internal sealed class WebView2InstallException : Exception
    {
        public WebView2InstallException(string message) : base(message) { }
    }

    internal sealed class InstallerException : Exception
    {
        public InstallerException(string message, Exception inner) : base(message, inner) { }
    }

    internal static class InstallerEngine
    {
        public const string AppExeName = "Max.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string ProductName = "MAX Messenger";
        public const string Version = "1.0.2";
        public const string RegistryUninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MAXMessenger";
        public const string RegistryAppKey = @"Software\MAXMessenger";

        private const string PayloadResource = "payload.zip";
        private const string StagingPrefix = ".max-staging-";
        private const string BackupPrefix = ".max-backup-";
        private const string TempMarker = ".max-temp";

        public static string DefaultInstallDir
        {
            get
            {
                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                    return Path.Combine(local, "Programs", "MAX");

                string root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) root = @"C:\";
                return Path.Combine(root, "Max");
            }
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

            path = path.TrimEnd('\\');
            SafeFileSystem.EnsureSafeRoot(path);
            return path;
        }

        public static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
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
                Logger.Write("Elevation cancelled or failed: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
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
                Logger.Write("Failed to launch MAX: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
            }
        }

        public static bool IsWebView2Installed()
        {
            return WebView2Installer.IsInstalled();
        }

        public static bool IsExistingInstall(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return false;

            string exe = Path.Combine(dir, AppExeName);
            if (!File.Exists(exe))
                return false;

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                return string.Equals(info.ProductName, ProductName,
                                     StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(info.CompanyName, "MAX",
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool HasForeignFiles(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;

            // Папка с уже установленным MAX обновляется на месте, даже если
            // пользователь положил туда свои файлы.
            if (IsExistingInstall(dir))
                return false;

            InstallManifest manifest = InstallManifest.TryLoad(dir);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (manifest != null)
            {
                foreach (string file in manifest.Files)
                    known.Add(file);
            }
            else
            {
                foreach (string file in InstallManifest.CreateLegacy(dir).Files)
                    known.Add(file);
            }

            known.Add(InstallManifest.FileName);

            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(dir))
                {
                    string name = Path.GetFileName(entry);
                    if (name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, TempMarker, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (SafeFileSystem.IsReparsePoint(entry))
                        return true;

                    if (File.Exists(entry))
                    {
                        string relative = SafeFileSystem.Relative(dir, entry);
                        if (!known.Contains(relative))
                            return true;
                    }
                    else if (Directory.Exists(entry))
                    {
                        if (!IsKnownDirectory(dir, entry, known))
                            return true;
                    }
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        private static bool IsKnownDirectory(string root, string directory, HashSet<string> known)
        {
            try
            {
                string[] files = SafeFileSystem.GetRelativeFiles(directory);
                if (files.Length == 0)
                    return SafeFileSystem.IsDirectoryEmpty(directory);

                foreach (string relative in files)
                {
                    string rootRelative = SafeFileSystem.Relative(root, Path.Combine(directory, relative));
                    if (!known.Contains(rootRelative))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static InstallResult Install(
            string dir,
            bool desktopShortcut,
            bool allowWithoutWebView2,
            IProgress<InstallProgress> progress)
        {
            dir = NormalizeInstallDir(dir);

            bool existed = Directory.Exists(dir);
            bool wasInstalled = existed && File.Exists(Path.Combine(dir, AppExeName));
            InstallManifest oldManifest = existed ? InstallManifest.TryLoad(dir) : null;
            if (oldManifest == null && wasInstalled)
                oldManifest = InstallManifest.CreateLegacy(dir);

            Report(progress, 0, "Подготовка...");
            EnsureWritable(dir);
            CloseRunningApp(dir);

            WebView2Result webView2 = WebView2Installer.Ensure(progress);
            if (!webView2.Installed && !allowWithoutWebView2)
                throw new WebView2InstallException(webView2.Message);

            string staging = Path.Combine(
                dir, StagingPrefix + Guid.NewGuid().ToString("N"));
            string backup = Path.Combine(
                dir, BackupPrefix + Guid.NewGuid().ToString("N"));

            var replaced = new List<string>();
            var created = new List<string>();

            try
            {
                SafeFileSystem.EnsureDirectory(dir, staging);
                SafeFileSystem.EnsureDirectory(dir, backup);
                File.WriteAllText(Path.Combine(staging, TempMarker), "staging");
                File.WriteAllText(Path.Combine(backup, TempMarker), "backup");

                Report(progress, 20, "Распаковка файлов...");
                ExtractPayload(staging, progress, 20, 62);

                Report(progress, 65, "Подготовка удаления...");
                CopyUninstaller(staging);

                string[] newFiles = FilterTempMarkers(SafeFileSystem.GetRelativeFiles(staging));
                var allFiles = new List<string>(newFiles);

                if (oldManifest != null)
                {
                    foreach (string oldFile in oldManifest.Files)
                    {
                        string full = Path.Combine(dir, oldFile);
                        if (File.Exists(full) &&
                            !Contains(allFiles, oldFile))
                            allFiles.Add(oldFile);
                    }
                }

                string manifestId = oldManifest != null
                    ? oldManifest.Id
                    : Guid.NewGuid().ToString("N");

                InstallManifest manifest = InstallManifest.Create(
                    manifestId, Version, allFiles);
                if (!Contains(manifest.Files, InstallManifest.FileName))
                    manifest.Files.Add(InstallManifest.FileName);
                manifest.Files.Sort(StringComparer.OrdinalIgnoreCase);
                manifest.Save(staging);

                newFiles = FilterTempMarkers(SafeFileSystem.GetRelativeFiles(staging));

                Report(progress, 68, "Сохранение предыдущей версии...");

                foreach (string relative in newFiles)
                {
                    string source = SafeFileSystem.CombineRelative(staging, relative);
                    string target = SafeFileSystem.CombineRelative(dir, relative);

                    SafeFileSystem.EnsureFileTarget(dir, target);

                    if (File.Exists(target))
                    {
                        string backupPath = SafeFileSystem.CombineRelative(backup, relative);
                        string backupParent = Path.GetDirectoryName(backupPath);
                        if (!string.IsNullOrEmpty(backupParent))
                            SafeFileSystem.EnsureDirectory(backup, backupParent);

                        File.Replace(source, target, backupPath, true);
                        replaced.Add(relative);
                    }
                    else
                    {
                        File.Move(source, target);
                        created.Add(relative);
                    }
                }

                Report(progress, 82, "Проверка установленных файлов...");
                if (!File.Exists(Path.Combine(dir, AppExeName)))
                    throw new IOException("Не найден основной файл приложения после установки.");
            }
            catch (Exception ex)
            {
                Logger.Write("Install/update failed: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);

                bool restored = Rollback(dir, staging, backup, replaced, created);
                throw new InstallerException(
                    "Не удалось установить или обновить MAX. " +
                    (restored
                        ? "Предыдущая версия сохранена."
                        : "Не удалось полностью восстановить предыдущую версию; " +
                          "запустите установщик повторно."),
                    ex);
            }

            Report(progress, 86, "Удаление временных файлов...");
            try
            {
                SafeFileSystem.DeleteKnownTree(dir, staging);
                SafeFileSystem.DeleteKnownTree(dir, backup);
            }
            catch (Exception ex)
            {
                Logger.Write("Temporary files cleanup failed: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8"));
            }

            var result = new InstallResult
            {
                InstallDir = dir,
                Updated = wasInstalled,
                WebView2Available = webView2.Installed,
                WebView2Attempted = webView2.Attempted
            };

            Report(progress, 90, "Создание ярлыков...");
            if (!CreateShortcuts(dir, desktopShortcut))
                result.Warning = "Не удалось создать ярлыки. Файлы программы установлены.";

            Report(progress, 94, "Регистрация программы...");
            try
            {
                WriteRegistry(dir);
            }
            catch (Exception ex)
            {
                Logger.Write("Registry registration failed: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                result.Warning = AppendWarning(
                    result.Warning,
                    "Не удалось добавить запись в список установленных программ.");
            }

            if (!webView2.Installed)
                result.Warning = AppendWarning(result.Warning, webView2.Message);

            Report(progress, 100, "Установка завершена");
            return result;
        }

        private static bool Rollback(
            string dir,
            string staging,
            string backup,
            List<string> replaced,
            List<string> created)
        {
            bool restored = true;

            for (int i = replaced.Count - 1; i >= 0; i--)
            {
                string relative = replaced[i];
                try
                {
                    string target = SafeFileSystem.CombineRelative(dir, relative);
                    string backupPath = SafeFileSystem.CombineRelative(backup, relative);

                    if (!File.Exists(backupPath))
                        continue;

                    if (File.Exists(target))
                    {
                        try { File.Replace(backupPath, target, null, true); }
                        catch { File.Copy(backupPath, target, true); }
                    }
                    else
                    {
                        File.Move(backupPath, target);
                    }
                }
                catch (Exception ex)
                {
                    restored = false;
                    Logger.Write("Rollback failed for one file: " + ex.GetType().Name +
                                 " 0x" + ex.HResult.ToString("X8"));
                }
            }

            for (int i = created.Count - 1; i >= 0; i--)
            {
                try
                {
                    string target = SafeFileSystem.CombineRelative(dir, created[i]);
                    SafeFileSystem.DeleteFile(dir, target);
                }
                catch (Exception ex)
                {
                    restored = false;
                    Logger.Write("Rollback cleanup failed: " + ex.GetType().Name +
                                 " 0x" + ex.HResult.ToString("X8"));
                }
            }

            if (restored)
            {
                try { SafeFileSystem.DeleteKnownTree(dir, staging); } catch { }
                try { SafeFileSystem.DeleteKnownTree(dir, backup); } catch { }
            }
            else
            {
                Logger.Write("Rollback backup left in place for manual recovery");
            }

            return restored;
        }

        public static UninstallResult Uninstall(
            string dir,
            bool keepUserData,
            string profileDir,
            IProgress<InstallProgress> progress)
        {
            if (string.IsNullOrEmpty(dir))
                throw new ArgumentException("Не найдена папка установки.");

            dir = NormalizeInstallDir(dir);
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException("Папка установки не найдена.");

            Report(progress, 0, "Удаление MAX...");
            CloseRunningApp(dir);

            Report(progress, 8, "Удаление ярлыков...");
            DeleteShortcuts(dir);

            Report(progress, 14, "Удаление записей реестра...");
            DeleteRegistry();

            InstallManifest manifest = InstallManifest.TryLoad(dir);
            bool manifestExisted = manifest != null;
            if (manifest == null)
                manifest = InstallManifest.CreateLegacy(dir);

            bool selfInside = IsSelfInsideDirectory(dir);
            var deletedFiles = new List<string>();

            // Для фоновой очистки манифест должен лежать на диске: процесс
            // очистки проверяет его идентификатор и список файлов.
            if (selfInside && !manifestExisted)
            {
                try
                {
                    manifest.Save(dir);
                    manifestExisted = true;
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to persist legacy manifest: " +
                                 ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                }
            }

            Report(progress, 24, "Удаление файлов...");
            foreach (string relative in manifest.Files)
            {
                if (string.Equals(relative, InstallManifest.FileName,
                                  StringComparison.OrdinalIgnoreCase))
                    continue;

                if (selfInside &&
                    string.Equals(relative, UninstallerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    string target = SafeFileSystem.CombineRelative(dir, relative);
                    SafeFileSystem.DeleteFile(dir, target);
                    deletedFiles.Add(relative);
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to delete an application file: " +
                                 ex.GetType().Name + " 0x" +
                                 ex.HResult.ToString("X8") + " " + ex.Message);
                }
            }

            Report(progress, 58, "Удаление временных файлов...");
            DeleteKnownTempDirectories(dir);

            foreach (string relative in deletedFiles)
            {
                try
                {
                    SafeFileSystem.RemoveEmptyParents(
                        dir, SafeFileSystem.CombineRelative(dir, relative));
                }
                catch { }
            }

            var result = new UninstallResult
            {
                InstallDir = dir,
                UserDataPreserved = keepUserData
            };

            if (!keepUserData)
            {
                Report(progress, 72, "Удаление сохранённого профиля...");
                result.UserDataDeleted = DeleteUserData(profileDir);
                result.UserDataPreserved = !result.UserDataDeleted;
            }

            if (selfInside)
            {
                Report(progress, 84, "Завершение удаления...");
                result.CleanupScheduled = ScheduleDirectoryCleanup(dir, manifest.Id);
                result.FolderPreserved = true;
            }
            else
            {
                Report(progress, 84, "Завершение удаления...");
                try
                {
                    string manifestPath = Path.Combine(dir, InstallManifest.FileName);
                    SafeFileSystem.DeleteFile(dir, manifestPath);
                    SafeFileSystem.RemoveEmptyParents(dir, manifestPath);
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to delete install manifest: " +
                                 ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                }

                TryDeleteRootIfEmpty(dir, result);
            }

            Report(progress, 100, "MAX удалён");
            return result;
        }

        private static void TryDeleteRootIfEmpty(string dir, UninstallResult result)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    result.FolderPreserved = false;
                    return;
                }

                if (SafeFileSystem.IsDirectoryEmpty(dir))
                {
                    Directory.Delete(dir, false);
                    result.FolderPreserved = false;
                }
                else
                {
                    result.FolderPreserved = true;
                    Logger.Write("Install directory kept because it contains other files");
                }
            }
            catch (Exception ex)
            {
                result.FolderPreserved = true;
                Logger.Write("Failed to delete install directory: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            }
        }

        private static void DeleteKnownTempDirectories(string dir)
        {
            try
            {
                foreach (string child in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(child);
                    bool known = name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);
                    if (!known)
                        continue;

                    if (!File.Exists(Path.Combine(child, TempMarker)) &&
                        !SafeFileSystem.IsDirectoryEmpty(child))
                        continue;

                    SafeFileSystem.DeleteKnownTree(dir, child);
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to remove temporary directories: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            }
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

        public static bool IsAppRunning(string dir)
        {
            string expected = Path.Combine(dir, AppExeName);
            try
            {
                foreach (Process process in Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(AppExeName)))
                {
                    try
                    {
                        string path = process.MainModule.FileName;
                        if (SafeFileSystem.PathsEqual(path, expected))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Закрывает только Max.exe, расположенный в папке установки.
        /// Сторонний процесс с именем Max не затрагивается.
        /// </summary>
        public static void CloseRunningApp(string dir)
        {
            string expected = Path.Combine(dir, AppExeName);

            try
            {
                foreach (Process process in Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(AppExeName)))
                {
                    try
                    {
                        string path = process.MainModule.FileName;
                        if (!SafeFileSystem.PathsEqual(path, expected))
                            continue;

                        bool requested = false;
                        try { requested = process.CloseMainWindow(); }
                        catch { }

                        if (requested)
                            process.WaitForExit(8000);

                        if (!process.HasExited)
                        {
                            try { process.Kill(); } catch { }
                            process.WaitForExit(5000);
                        }

                        if (!process.HasExited)
                            throw new IOException(
                                "Не удалось закрыть запущенный MAX. Закройте его вручную.");
                    }
                    catch (IOException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Write("Failed to close MAX process: " +
                                     ex.GetType().Name + " 0x" +
                                     ex.HResult.ToString("X8"));
                    }
                }
            }
            catch (IOException) { throw; }
            catch (Exception ex)
            {
                Logger.Write("Failed to enumerate MAX processes: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
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
                Logger.Write("Start menu shortcut failed: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8"));
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
                    Logger.Write("Desktop shortcut failed: " + ex.GetType().Name +
                                 " 0x" + ex.HResult.ToString("X8"));
                }
            }

            return ok;
        }

        public static void DeleteShortcuts(string dir)
        {
            string target = Path.Combine(dir, AppExeName);
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
                    ShortcutHelper.DeleteIfTargets(path, target);
                }
                catch (Exception ex)
                {
                    Logger.Write("Failed to delete shortcut: " + ex.GetType().Name +
                                 " 0x" + ex.HResult.ToString("X8"));
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
                foreach (string relative in SafeFileSystem.GetRelativeFiles(dir))
                {
                    try
                    {
                        size += new FileInfo(Path.Combine(dir, relative)).Length;
                    }
                    catch { }
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
                Logger.Write("Failed to delete uninstall registry key: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryAppKey, false);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to delete app registry key: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            }
        }

        public static int CleanupDirectory(string dir, string expectedId)
        {
            if (string.IsNullOrEmpty(dir)) return 1;

            try
            {
                dir = NormalizeInstallDir(dir);
            }
            catch (Exception ex)
            {
                Logger.Write("Cleanup rejected invalid directory: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") +
                             " " + ex.Message);
                return 1;
            }

            for (int attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return 0;

                    InstallManifest manifest = InstallManifest.TryLoad(dir);
                    if (manifest == null || manifest.Files.Count == 0)
                    {
                        Logger.Write("Cleanup rejected: install manifest missing");
                        return 1;
                    }

                    if (!string.IsNullOrEmpty(expectedId) &&
                        !string.Equals(manifest.Id, expectedId,
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Write("Cleanup rejected: manifest id mismatch");
                        return 1;
                    }

                    foreach (string relative in manifest.Files)
                    {
                        if (string.Equals(relative, InstallManifest.FileName,
                                          StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            SafeFileSystem.DeleteFile(
                                dir, SafeFileSystem.CombineRelative(dir, relative));
                        }
                        catch (Exception ex)
                        {
                            Logger.Write("Cleanup file delete failed: " +
                                         ex.GetType().Name + " 0x" +
                                         ex.HResult.ToString("X8"));
                        }
                    }

                    DeleteKnownTempDirectories(dir);

                    try
                    {
                        SafeFileSystem.DeleteFile(
                            dir, Path.Combine(dir, InstallManifest.FileName));
                    }
                    catch { }

                    if (SafeFileSystem.IsDirectoryEmpty(dir))
                    {
                        Directory.Delete(dir, false);
                        Logger.Write("Cleanup removed install directory");
                        return 0;
                    }

                    Logger.Write("Cleanup left directory because it is not empty");
                    return 0;
                }
                catch (Exception ex)
                {
                    if (attempt == 0 || attempt % 10 == 0)
                        Logger.Write("Cleanup retry: " + ex.GetType().Name +
                                     " 0x" + ex.HResult.ToString("X8"));
                    Thread.Sleep(500);
                }
            }

            Logger.Write("Cleanup failed");
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
                Logger.Write("Failed to schedule self delete: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            }
        }

        private static bool ScheduleDirectoryCleanup(string dir, string manifestId)
        {
            try
            {
                string temp = Path.Combine(
                    Path.GetTempPath(),
                    "MaxCleanup_" + Guid.NewGuid().ToString("N") + ".exe");

                File.Copy(Application.ExecutablePath, temp, true);

                var info = new ProcessStartInfo(
                    temp,
                    "/cleanup=\"" + dir + "\" /cleanupid=" + manifestId);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                Process.Start(info);

                Logger.Write("Scheduled directory cleanup");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to schedule directory cleanup: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                return false;
            }
        }

        private static bool IsSelfInsideDirectory(string dir)
        {
            try
            {
                return SafeFileSystem.IsWithinRoot(dir, Application.ExecutablePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool DeleteUserData(string profileDir)
        {
            string root = profileDir;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MAX Messenger");
            }

            try
            {
                root = Path.GetFullPath(root).TrimEnd('\\');
                if (!Directory.Exists(root))
                    return false;

                if (!string.Equals(Path.GetFileName(root), "MAX Messenger",
                                   StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Write("Profile deletion rejected: unexpected directory name");
                    return false;
                }

                string pathRoot = (Path.GetPathRoot(root) ?? string.Empty).TrimEnd('\\');
                if (string.Equals(root, pathRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Write("Profile deletion rejected: drive root");
                    return false;
                }

                SafeFileSystem.DeleteKnownTree(root, root);
                if (Directory.Exists(root) && SafeFileSystem.IsDirectoryEmpty(root))
                    Directory.Delete(root, false);

                return !Directory.Exists(root);
            }
            catch (Exception ex)
            {
                Logger.Write("Failed to delete user data: " +
                             ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                return false;
            }
        }

        private static void EnsureWritable(string dir)
        {
            Directory.CreateDirectory(dir);

            string probe = Path.Combine(
                dir, ".max-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.WriteByte(1);
            }
            File.Delete(probe);
        }

        private static void ExtractPayload(
            string staging,
            IProgress<InstallProgress> progress,
            int fromPercent,
            int toPercent)
        {
            using (Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(PayloadResource))
            {
                if (stream == null)
                    throw new InvalidOperationException(
                        "В установщике отсутствуют файлы приложения.");

                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, archive.Entries.Count);
                    int done = 0;

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(
                            Path.Combine(staging, entry.FullName));

                        if (!SafeFileSystem.IsLexicallyWithin(staging, target))
                            throw new IOException("Повреждён установочный архив.");

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            SafeFileSystem.EnsureDirectory(staging, target);
                        }
                        else
                        {
                            SafeFileSystem.EnsureFileTarget(staging, target);
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

        private static void CopyUninstaller(string staging)
        {
            string source = Path.GetFullPath(Application.ExecutablePath);
            string target = Path.GetFullPath(Path.Combine(staging, UninstallerName));

            SafeFileSystem.EnsureFileTarget(staging, target);

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return;

            File.Copy(source, target, true);
        }

        private static string[] FilterTempMarkers(string[] files)
        {
            var result = new List<string>();
            foreach (string file in files)
            {
                if (string.Equals(file, TempMarker, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(file);
            }
            return result.ToArray();
        }

        private static bool Contains(List<string> list, string value)
        {
            foreach (string item in list)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string AppendWarning(string existing, string warning)
        {
            if (string.IsNullOrEmpty(existing))
                return warning;
            return existing + "\n" + warning;
        }

        internal static void Report(
            IProgress<InstallProgress> progress,
            int percent,
            string text)
        {
            Logger.Write(percent + "% " + text);
            if (progress != null)
                progress.Report(new InstallProgress(percent, text));
        }
    }
}
