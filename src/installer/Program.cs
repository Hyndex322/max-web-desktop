using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using MaxCommon;

namespace MaxSetup
{
    internal sealed class SetupOptions
    {
        public bool Silent;
        public bool Uninstall;
        public bool DeleteData;
        public bool NoDesktop;
        public bool NoLaunch;
        public string Dir;
        public string CleanupDir;
        public string CleanupId;
        public string ProfileDir;

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
                        options.DeleteData = false;
                        break;
                    case "/deletedata":
                        options.DeleteData = true;
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
                    case "/cleanupid":
                        if (i + 1 < args.Length) options.CleanupId = args[++i];
                        break;
                    case "/profiledir":
                        if (i + 1 < args.Length) options.ProfileDir = args[++i];
                        break;
                    default:
                        if (lower.StartsWith("/dir="))
                        {
                            options.Dir = arg.Substring(5).Trim('"');
                        }
                        else if (lower.StartsWith("/cleanup="))
                        {
                            options.CleanupDir = arg.Substring(9).Trim('"');
                        }
                        else if (lower.StartsWith("/cleanupid="))
                        {
                            options.CleanupId = arg.Substring(11).Trim('"');
                        }
                        else if (lower.StartsWith("/profiledir="))
                        {
                            options.ProfileDir = arg.Substring(12).Trim('"');
                        }
                        break;
                }
            }

            return options;
        }

        public static string ResolveProfileDir(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return explicitPath;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MAX Messenger");
        }

        /// <summary>
        /// В журнал попадают только имена параметров, без путей.
        /// </summary>
        public string SafeSummary()
        {
            var parts = new List<string>();
            if (Silent) parts.Add("/silent");
            if (Uninstall) parts.Add("/uninstall");
            if (DeleteData) parts.Add("/deletedata");
            if (NoDesktop) parts.Add("/nodesktop");
            if (NoLaunch) parts.Add("/nolaunch");
            if (!string.IsNullOrEmpty(Dir)) parts.Add("/dir");
            if (!string.IsNullOrEmpty(CleanupDir)) parts.Add("/cleanup");
            if (!string.IsNullOrEmpty(CleanupId)) parts.Add("/cleanupid");
            if (!string.IsNullOrEmpty(ProfileDir)) parts.Add("/profiledir");
            return parts.Count == 0 ? "(none)" : string.Join(" ", parts.ToArray());
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
                            LogSanitizer.Sanitize(message),
                            Environment.NewLine));
                }
            }
            catch { }
        }
    }
}
