using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MaxSetup
{
    internal sealed class SetupOptions
    {
        public bool Silent;
        public bool Uninstall;
        public bool KeepData;
        public bool NoDesktop;
        public bool NoLaunch;
        public string Dir;
        public string CleanupDir;

        public static SetupOptions Parse(string[] args)
        {
            var options = new SetupOptions();

            string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath) ?? string.Empty;
            if (exeName.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
                options.Uninstall = true;

            if (args == null) return options;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = (args[i] ?? string.Empty).Trim();
                string lower = arg.ToLowerInvariant();

                switch (lower)
                {
                    case "/silent":
                    case "/s":
                        options.Silent = true;
                        break;
                    case "/uninstall":
                        options.Uninstall = true;
                        break;
                    case "/keepdata":
                        options.KeepData = true;
                        break;
                    case "/nodesktop":
                        options.NoDesktop = true;
                        break;
                    case "/nolaunch":
                        options.NoLaunch = true;
                        break;
                    case "/dir":
                        if (i + 1 < args.Length) options.Dir = args[++i];
                        break;
                    case "/cleanup":
                        if (i + 1 < args.Length) options.CleanupDir = args[++i];
                        break;
                    default:
                        if (lower.StartsWith("/dir="))
                            options.Dir = arg.Substring(5).Trim('"');
                        else if (lower.StartsWith("/cleanup="))
                            options.CleanupDir = arg.Substring(9).Trim('"');
                        break;
                }
            }

            return options;
        }
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _file;

        public static string FilePath
        {
            get { return _file ?? Path.Combine(Path.GetTempPath(), "MAX-Setup.log"); }
        }

        public static void Init()
        {
            try
            {
                _file = Path.Combine(Path.GetTempPath(), "MAX-Setup.log");
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    try { File.Delete(_file); } catch { }
                }
                Write("--- MAX Setup session started ---");
            }
            catch
            {
                _file = Path.Combine(Path.GetTempPath(), "MAX-Setup.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(
                        FilePath,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:yyyy-MM-dd HH:mm:ss.fff} {1}{2}",
                            DateTime.Now,
                            message,
                            Environment.NewLine));
                }
            }
            catch { }
        }
    }
}
