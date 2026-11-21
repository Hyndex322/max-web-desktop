using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MaxMessenger
{
    internal static class Program
    {
        private const string MutexName = "Local\\MAX.Messenger.SingleInstance.v1";

        [STAThread]
        private static int Main(string[] args)
        {
            bool selfTest = HasArg(args, "/selftest");
            string resultPath = selfTest
                ? Path.Combine(Path.GetTempPath(), "max-selftest-result.txt")
                : null;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Logger.Init();
            Logger.Info("=== MAX Messenger " + AppInfo.Version + " starting ===");

            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
            Application.ThreadException += OnThreadException;

            try
            {
                ConfigureWebView2Loader();
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 loader configuration failed", ex);
            }

            if (selfTest)
            {
                try { File.Delete(resultPath); } catch { }
                var form = new MainForm(true, resultPath);
                Application.Run(form);
                return form.ExitCode;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    ActivateExistingWindow();
                    return 0;
                }

                var form = new MainForm(false, null);
                Application.Run(form);
                return form.ExitCode;
            }
        }

        private static void ConfigureWebView2Loader()
        {
            // WebView2Loader.dll is shipped per architecture in runtimes\<arch>.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty)
                .ToUpperInvariant();

            string folder;
            if (arch == "ARM64")
                folder = "arm64";
            else if (Environment.Is64BitProcess)
                folder = "x64";
            else
                folder = "x86";

            string path = Path.Combine(baseDir, "runtimes", folder);
            if (Directory.Exists(path))
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(path);
                Logger.Info("WebView2 loader path: " + path);
            }
        }

        private static void ActivateExistingWindow()
        {
            try
            {
                IntPtr handle = NativeMethods.FindWindow(null, AppInfo.WindowTitle);
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(handle);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to activate existing window", ex);
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Logger.Error("Unhandled UI exception", e.Exception);
            ShowFatalError(e.Exception);
        }

        private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Error("Unhandled application exception", e.ExceptionObject as Exception);
        }

        private static void ShowFatalError(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "Произошла непредвиденная ошибка.\n\n" + ex.Message +
                    "\n\nПодробности записаны в журнал:\n" + Logger.LogDirectory,
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { }
        }

        private static bool HasArg(string[] args, string value)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    internal static class AppInfo
    {
        public const string Name = "MAX";
        public const string WindowTitle = "MAX";
        public const string Version = "1.0.0";
        public const string StartUrl = "https://web.max.ru/";
    }

    internal static class AppPaths
    {
        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MAX Messenger");
            }
        }

        public static string UserDataDir { get { return Path.Combine(Root, "WebView2"); } }
        public static string SettingsFile { get { return Path.Combine(Root, "settings.ini"); } }
    }

    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr handle, int command);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(
            IntPtr window, int attribute, ref int value, int size);
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _directory;
        private static string _file;

        public static string LogDirectory
        {
            get { return _directory ?? AppPaths.Root; }
        }

        public static void Init()
        {
            try
            {
                _directory = Path.Combine(AppPaths.Root, "logs");
                Directory.CreateDirectory(_directory);
                _file = Path.Combine(_directory, "app.log");

                var info = new FileInfo(_file);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    string old = Path.Combine(_directory, "app.old.log");
                    try { File.Delete(old); } catch { }
                    try { File.Move(_file, old); } catch { }
                }
            }
            catch
            {
                _directory = Path.GetTempPath();
                _file = Path.Combine(_directory, "max-app.log");
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                if (_file == null) Init();
                string line = string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}{4}",
                    DateTime.Now,
                    level,
                    message,
                    ex == null ? string.Empty : " :: " + ex.GetType().Name + ": " + ex.Message,
                    Environment.NewLine);

                lock (Sync)
                {
                    File.AppendAllText(_file, line);
                }
            }
            catch
            {
                // Logging must never break the application.
            }
        }
    }

    internal sealed class AppSettings
    {
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public int Width = 1100;
        public int Height = 800;
        public bool Maximized;

        public static AppSettings Load()
        {
            var result = new AppSettings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return result;

                foreach (string raw in File.ReadAllLines(AppPaths.SettingsFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();

                    switch (key)
                    {
                        case "X": result.X = ParseInt(value, result.X); break;
                        case "Y": result.Y = ParseInt(value, result.Y); break;
                        case "Width": result.Width = ParseInt(value, result.Width); break;
                        case "Height": result.Height = ParseInt(value, result.Height); break;
                        case "Maximized": result.Maximized = value == "1"; break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load settings", ex);
            }
            return result;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                string[] lines =
                {
                    "# MAX Messenger settings",
                    "X=" + X,
                    "Y=" + Y,
                    "Width=" + Width,
                    "Height=" + Height,
                    "Maximized=" + (Maximized ? "1" : "0")
                };
                File.WriteAllLines(AppPaths.SettingsFile, lines);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save settings", ex);
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }
    }
}
