using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MaxSetup
{
    /// <summary>
    /// Операции с файлами, которые не выходят за пределы папки установки.
    /// Учитывает символические ссылки, точки соединения и другие reparse points.
    /// </summary>
    internal static class SafeFileSystem
    {
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint VolumeNameDos = 0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        public static string TrimPath(string path)
        {
            return (path ?? string.Empty).TrimEnd('\\');
        }

        public static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return false;
            return string.Equals(
                TrimPath(Path.GetFullPath(left)),
                TrimPath(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLexicallyWithin(string root, string candidate)
        {
            string fullRoot = TrimPath(Path.GetFullPath(root));
            string fullCandidate = TrimPath(Path.GetFullPath(candidate));

            return string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetFinalPath(string path)
        {
            string full = Path.GetFullPath(path);
            string existing = full;
            string suffix = string.Empty;

            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                string parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || parent == existing)
                    break;

                suffix = string.IsNullOrEmpty(suffix)
                    ? Path.GetFileName(existing)
                    : Path.Combine(Path.GetFileName(existing), suffix);
                existing = parent;
            }

            string canonical = ResolveExisting(existing);
            if (!string.IsNullOrEmpty(suffix))
                canonical = Path.Combine(canonical, suffix);

            return Path.GetFullPath(canonical);
        }

        private static string ResolveExisting(string path)
        {
            try
            {
                using (SafeFileHandle handle = CreateFile(
                    path,
                    FileReadAttributes,
                    FileShareAll,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics,
                    IntPtr.Zero))
                {
                    if (handle.IsInvalid)
                        return Path.GetFullPath(path);

                    var buffer = new StringBuilder(1024);
                    uint length = GetFinalPathNameByHandle(
                        handle, buffer, (uint)buffer.Capacity, VolumeNameDos);

                    if (length == 0)
                        return Path.GetFullPath(path);

                    if (length >= buffer.Capacity)
                    {
                        buffer = new StringBuilder((int)length + 1);
                        length = GetFinalPathNameByHandle(
                            handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
                        if (length == 0)
                            return Path.GetFullPath(path);
                    }

                    string result = buffer.ToString();
                    if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                        result = @"\\" + result.Substring(8);
                    else if (result.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                        result = result.Substring(4);

                    return Path.GetFullPath(result);
                }
            }
            catch
            {
                return Path.GetFullPath(path);
            }
        }

        /// <summary>
        /// Проверяет, что путь остаётся внутри корня с учётом реального
        /// расположения всех существующих компонентов (включая ссылки).
        /// </summary>
        public static bool IsWithinRoot(string root, string candidate)
        {
            if (!IsLexicallyWithin(root, candidate))
                return false;

            string finalRoot = TrimPath(GetFinalPath(root));
            string finalCandidate = TrimPath(GetFinalPath(candidate));

            return string.Equals(finalRoot, finalCandidate, StringComparison.OrdinalIgnoreCase) ||
                   finalCandidate.StartsWith(finalRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static void EnsureSafeRoot(string root)
        {
            string full = TrimPath(Path.GetFullPath(root));
            string pathRoot = TrimPath(Path.GetPathRoot(full) ?? string.Empty);

            if (string.IsNullOrEmpty(full) ||
                string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Нельзя использовать корень диска как папку установки.");

            string windows = TrimPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.IsNullOrEmpty(windows) &&
                (string.Equals(full, windows, StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Нельзя устанавливать MAX в папку Windows.");

            string system = TrimPath(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (!string.IsNullOrEmpty(system) &&
                (string.Equals(full, system, StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(system + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Нельзя устанавливать MAX в системную папку.");

            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Нельзя устанавливать MAX в корень профиля пользователя.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Выберите отдельную папку MAX, а не корень LocalAppData.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Выберите отдельную папку MAX, а не корень AppData.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Выберите отдельную папку MAX, а не рабочий стол.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Выберите отдельную папку MAX, а не папку документов.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Выберите подпапку в Program Files, например Program Files\\MAX.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Выберите подпапку в Program Files, например Program Files\\MAX.");
            RejectExact(
                full,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Выберите отдельную папку MAX, а не корень ProgramData.");
            RejectExact(full, Path.GetTempPath(), "Выберите постоянную папку для установки.");

            if (Directory.Exists(full) && IsReparsePoint(full))
                throw new InvalidOperationException(
                    "Выбранная папка является ссылкой. Выберите обычную папку.");
        }

        private static void RejectExact(string full, string forbidden, string message)
        {
            if (string.IsNullOrEmpty(forbidden))
                return;

            if (string.Equals(
                full, TrimPath(Path.GetFullPath(forbidden)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(message);
        }

        public static void EnsureDirectory(string root, string directory)
        {
            if (PathsEqual(root, directory))
            {
                if (!Directory.Exists(root))
                    Directory.CreateDirectory(root);
                if (IsReparsePoint(root))
                    throw new IOException("Папка установки является ссылкой, операция отменена.");
                return;
            }

            if (!IsLexicallyWithin(root, directory))
                throw new IOException("Путь находится вне папки установки.");

            string parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrEmpty(parent))
                throw new IOException("Некорректный путь.");

            if (!Directory.Exists(parent))
                EnsureDirectory(root, parent);

            if (!IsWithinRoot(root, parent))
                throw new IOException("Путь выходит за пределы папки установки.");

            if (Directory.Exists(directory))
            {
                if (IsReparsePoint(directory))
                    throw new IOException("Папка установки содержит ссылку, операция отменена.");
                return;
            }

            if (File.Exists(directory))
                throw new IOException("На месте папки находится файл.");

            Directory.CreateDirectory(directory);

            if (!IsWithinRoot(root, directory))
                throw new IOException("Папка выходит за пределы папки установки.");
        }

        public static void EnsureFileTarget(string root, string file)
        {
            if (!IsLexicallyWithin(root, file))
                throw new IOException("Путь находится вне папки установки.");

            string parent = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(parent))
                throw new IOException("Некорректный путь.");

            EnsureDirectory(root, parent);

            if (File.Exists(file) && IsReparsePoint(file))
                throw new IOException("Файл установки является ссылкой, операция отменена.");

            if (!IsWithinRoot(root, parent) ||
                (!File.Exists(file) && !IsWithinRoot(root, file)))
                throw new IOException("Путь выходит за пределы папки установки.");
        }

        public static void DeleteFile(string root, string file)
        {
            if (!IsLexicallyWithin(root, file))
                throw new IOException("Путь удаления находится вне папки установки.");

            string parent = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(parent) || !IsWithinRoot(root, parent))
                throw new IOException("Путь удаления выходит за пределы папки установки.");

            if (!File.Exists(file) && !Directory.Exists(file))
                return;

            if (IsReparsePoint(file))
            {
                // Удаляем только саму ссылку, не следуя по ней.
                if (Directory.Exists(file))
                    Directory.Delete(file, false);
                else
                    File.Delete(file);
                return;
            }

            if (Directory.Exists(file))
                throw new IOException("Ожидался файл, найдена папка.");

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        public static void DeleteKnownTree(string root, string directory)
        {
            if (!IsLexicallyWithin(root, directory))
                throw new IOException("Путь удаления находится вне папки установки.");

            if (!Directory.Exists(directory))
                return;

            if (IsReparsePoint(directory))
            {
                Directory.Delete(directory, false);
                return;
            }

            foreach (string file in Directory.GetFiles(directory))
                DeleteFile(root, file);

            foreach (string child in Directory.GetDirectories(directory))
            {
                if (IsReparsePoint(child))
                    Directory.Delete(child, false);
                else
                    DeleteKnownTree(root, child);
            }

            TryDeleteEmptyDirectory(directory);
        }

        public static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path) || IsReparsePoint(path))
                    return;

                using (IEnumerator<string> entries =
                    Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    if (entries.MoveNext())
                        return;
                }

                Directory.Delete(path, false);
            }
            catch
            {
                // Каталог занят или не пуст — оставляем его.
            }
        }

        public static void RemoveEmptyParents(string root, string file)
        {
            try
            {
                string fullRoot = TrimPath(Path.GetFullPath(root));
                string current = Path.GetDirectoryName(Path.GetFullPath(file));

                while (!string.IsNullOrEmpty(current))
                {
                    if (string.Equals(TrimPath(current), fullRoot, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!IsLexicallyWithin(root, current))
                        return;

                    bool empty;
                    using (IEnumerator<string> entries =
                        Directory.EnumerateFileSystemEntries(current).GetEnumerator())
                    {
                        empty = !entries.MoveNext();
                    }

                    if (!empty)
                        return;

                    Directory.Delete(current, false);
                    current = Path.GetDirectoryName(current);
                }
            }
            catch
            {
                // Удаление пустых каталогов не критично.
            }
        }

        public static bool IsDirectoryEmpty(string path)
        {
            try
            {
                using (IEnumerator<string> entries =
                    Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    return !entries.MoveNext();
                }
            }
            catch
            {
                return false;
            }
        }

        public static string[] GetRelativeFiles(string root)
        {
            var result = new List<string>();
            CollectFiles(root, root, result);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result.ToArray();
        }

        private static void CollectFiles(string root, string directory, List<string> files)
        {
            foreach (string file in Directory.GetFiles(directory))
            {
                if (IsReparsePoint(file))
                    continue;

                files.Add(Relative(root, file));
            }

            foreach (string child in Directory.GetDirectories(directory))
            {
                if (IsReparsePoint(child))
                    continue;
                CollectFiles(root, child, files);
            }
        }

        public static string Relative(string root, string fullPath)
        {
            string fullRoot = TrimPath(Path.GetFullPath(root)) + "\\";
            string full = Path.GetFullPath(fullPath);

            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Файл находится вне папки установки.");

            return full.Substring(fullRoot.Length);
        }

        public static string CombineRelative(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) ||
                Path.IsPathRooted(relative) ||
                relative.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new IOException("Некорректный относительный путь.");

            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsLexicallyWithin(root, full))
                throw new IOException("Некорректный относительный путь.");

            return full;
        }

        public static void CopyDirectoryNoFollow(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                if (IsReparsePoint(file))
                    continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (string child in Directory.GetDirectories(source))
            {
                if (IsReparsePoint(child))
                    continue;
                CopyDirectoryNoFollow(child, Path.Combine(destination, Path.GetFileName(child)));
            }
        }
    }
}
