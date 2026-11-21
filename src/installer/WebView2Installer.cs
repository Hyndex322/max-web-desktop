using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace MaxSetup
{
    internal sealed class WebView2Result
    {
        public bool Installed;
        public bool Attempted;
        public string Message;
    }

    internal static class WebView2Installer
    {
        private const string ResourceName = "webview2setup.exe";
        private const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const int BootstrapperTimeoutMs = 5 * 60 * 1000;

        public static bool IsInstalled()
        {
            string[] subKeys =
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientId,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientId
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

            return HasRuntimeFiles();
        }

        private static bool HasRuntimeFiles()
        {
            try
            {
                string[] roots =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                };

                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string application = Path.Combine(root, "Microsoft", "EdgeWebView", "Application");
                    if (!Directory.Exists(application)) continue;

                    foreach (string versionDir in Directory.GetDirectories(application))
                    {
                        if (File.Exists(Path.Combine(versionDir, "msedgewebview2.exe")))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        public static WebView2Result Ensure(IProgress<InstallProgress> progress)
        {
            var result = new WebView2Result();

            if (IsInstalled())
            {
                result.Installed = true;
                result.Message = "Компонент Microsoft Edge WebView2 уже установлен.";
                return result;
            }

            result.Attempted = true;
            InstallerEngine.Report(progress, 5, "Установка компонента Microsoft Edge WebView2...");

            string tempDirectory = null;
            try
            {
                tempDirectory = CreateRestrictedTempDirectory();
                string bootstrapper = Path.Combine(tempDirectory, "MicrosoftEdgeWebview2Setup.exe");

                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        result.Message = "В установщике отсутствует загрузчик WebView2.";
                        return result;
                    }

                    using (var file = new FileStream(
                        bootstrapper, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.CopyTo(file);
                    }
                }

                string signatureError;
                if (!SignatureVerifier.VerifyMicrosoftFile(bootstrapper, out signatureError))
                {
                    Logger.Write("WebView2 bootstrapper signature rejected: " + signatureError);
                    result.Message =
                        "Подпись встроенного загрузчика WebView2 недействительна. " +
                        "Установка компонента отменена. Установите Microsoft Edge WebView2 " +
                        "с официального сайта Microsoft и повторите установку MAX.";
                    return result;
                }

                Logger.Write("WebView2 bootstrapper signature verified (Microsoft Corporation)");

                var info = new ProcessStartInfo(bootstrapper, "/silent /install");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = tempDirectory;

                int exitCode = -1;
                bool timedOut = false;

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        result.Message = "Не удалось запустить установщик WebView2.";
                        return result;
                    }

                    if (!process.WaitForExit(BootstrapperTimeoutMs))
                    {
                        timedOut = true;
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(5000); } catch { }
                    }
                    else
                    {
                        exitCode = process.ExitCode;
                    }
                }

                if (timedOut)
                {
                    Logger.Write("WebView2 bootstrapper timed out");
                    result.Message =
                        "Установка Microsoft Edge WebView2 заняла слишком много времени. " +
                        "Проверьте подключение к интернету и повторите установку.";
                    return result;
                }

                Logger.Write("WebView2 bootstrapper exit code: " + exitCode);

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (IsInstalled())
                    {
                        result.Installed = true;
                        result.Message = "Microsoft Edge WebView2 установлен.";
                        return result;
                    }
                    Thread.Sleep(500);
                }

                if (exitCode != 0 && exitCode != 3010 && exitCode != 1641)
                {
                    result.Message =
                        "Не удалось установить Microsoft Edge WebView2 (код " + exitCode + "). " +
                        "Проверьте подключение к интернету и повторите установку. " +
                        "Компонент можно установить вручную с сайта Microsoft.";
                }
                else
                {
                    result.Message =
                        "Microsoft Edge WebView2 не обнаружен после установки. " +
                        "Проверьте подключение к интернету и повторите установку. " +
                        "Компонент можно установить вручную с сайта Microsoft.";
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Write("WebView2 bootstrap failed: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                result.Message =
                    "Не удалось установить Microsoft Edge WebView2. " +
                    "Проверьте подключение к интернету и повторите установку. " +
                    "Компонент можно установить вручную с сайта Microsoft.";
                return result;
            }
            finally
            {
                if (tempDirectory != null)
                    TryDeleteDirectory(tempDirectory);
            }
        }

        private static string CreateRestrictedTempDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "MAX-WebView2-" + Guid.NewGuid().ToString("N"));

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);

            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity != null && identity.User != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            Directory.CreateDirectory(path, security);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    Directory.Delete(path, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }
    }
}
