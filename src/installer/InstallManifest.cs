using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MaxSetup
{
    /// <summary>
    /// Список файлов, установленных приложением. Используется при обновлении
    /// и удалении, чтобы не трогать посторонние файлы пользователя.
    /// </summary>
    internal sealed class InstallManifest
    {
        public const string FileName = ".max-install-manifest";
        private const string Header = "MAX-INSTALL-MANIFEST 1";

        public string Id = Guid.NewGuid().ToString("N");
        public string Version = "1.0.0";
        public readonly List<string> Files = new List<string>();

        public static InstallManifest TryLoad(string root)
        {
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path))
                return null;

            try
            {
                var manifest = new InstallManifest();
                string[] lines = File.ReadAllLines(path);

                if (lines.Length == 0 ||
                    !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
                    return null;

                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    if (line.StartsWith("Id=", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = line.Substring(3).Trim();
                        Guid parsed;
                        if (Guid.TryParseExact(id, "N", out parsed))
                            manifest.Id = id;
                    }
                    else if (line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase))
                    {
                        manifest.Version = line.Substring(8).Trim();
                    }
                    else if (line.StartsWith("File=", StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = line.Substring(5).Trim().Replace('/', '\\');
                        if (IsSafeRelative(root, relative) &&
                            !HasFile(manifest.Files, relative))
                            manifest.Files.Add(relative);
                    }
                }

                return manifest.Files.Count > 0 ? manifest : null;
            }
            catch
            {
                return null;
            }
        }

        public static InstallManifest CreateLegacy(string root)
        {
            var manifest = new InstallManifest();
            manifest.Version = "1.0.0";

            string[] known =
            {
                "Max.exe",
                "Uninstall.exe",
                "README.txt",
                "Microsoft.Web.WebView2.Core.dll",
                "Microsoft.Web.WebView2.WinForms.dll",
                "runtimes\\x86\\WebView2Loader.dll",
                "runtimes\\x64\\WebView2Loader.dll",
                "runtimes\\arm64\\WebView2Loader.dll"
            };

            foreach (string relative in known)
            {
                if (File.Exists(Path.Combine(root, relative)) &&
                    !HasFile(manifest.Files, relative))
                    manifest.Files.Add(relative);
            }

            return manifest;
        }

        public static InstallManifest Create(string id, string version, IEnumerable<string> files)
        {
            var manifest = new InstallManifest();
            if (!string.IsNullOrEmpty(id))
                manifest.Id = id;
            manifest.Version = version ?? "1.0.1";

            foreach (string file in files)
            {
                string relative = (file ?? string.Empty).Replace('/', '\\').Trim();
                if (relative.Length == 0)
                    continue;

                if (!HasFile(manifest.Files, relative))
                    manifest.Files.Add(relative);
            }

            manifest.Files.Sort(StringComparer.OrdinalIgnoreCase);
            return manifest;
        }

        public void Save(string root)
        {
            string path = Path.Combine(root, FileName);
            SafeFileSystem.EnsureFileTarget(root, path);

            var builder = new StringBuilder();
            builder.AppendLine(Header);
            builder.AppendLine("Id=" + Id);
            builder.AppendLine("Version=" + Version);
            builder.AppendLine("# relative paths; do not edit");
            foreach (string file in Files)
                builder.AppendLine("File=" + file.Replace('\\', '/'));

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        public static bool IsSafeRelative(string root, string relative)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relative) ||
                    Path.IsPathRooted(relative) ||
                    relative.IndexOf("..", StringComparison.Ordinal) >= 0)
                    return false;

                string full = Path.GetFullPath(Path.Combine(root, relative));
                return SafeFileSystem.IsLexicallyWithin(root, full);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasFile(List<string> files, string relative)
        {
            foreach (string file in files)
            {
                if (string.Equals(file, relative, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
