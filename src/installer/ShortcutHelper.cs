using System;
using System.IO;
using System.Threading;

namespace MaxSetup
{
    internal static class ShortcutHelper
    {
        public static void Create(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description)
        {
            Exception failure = null;

            // WScript.Shell is an apartment-threaded COM object, so create it on STA.
            var thread = new Thread(delegate()
            {
                try
                {
                    CreateCore(shortcutPath, targetPath, workingDirectory, description);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(30000);

            if (failure != null)
            {
                Logger.Write("Shortcut creation failed: " + failure.GetType().Name +
                             " 0x" + failure.HResult.ToString("X8"));
                throw new IOException("Не удалось создать ярлык.", failure);
            }

            if (!File.Exists(shortcutPath))
                throw new IOException("Ярлык не был создан.");

            Logger.Write("Shortcut created");
        }

        /// <summary>
        /// Удаляет ярлык, только если он указывает на наш Max.exe.
        /// Посторонний MAX.lnk пользователя не затрагивается.
        /// </summary>
        public static void DeleteIfTargets(string shortcutPath, string expectedTarget)
        {
            if (!File.Exists(shortcutPath))
                return;

            bool matches = false;
            Exception failure = null;

            var thread = new Thread(delegate()
            {
                try
                {
                    string target = ReadTargetCore(shortcutPath);
                    matches = !string.IsNullOrEmpty(target) &&
                              string.Equals(
                                  Path.GetFullPath(target),
                                  Path.GetFullPath(expectedTarget),
                                  StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(15000);

            if (failure != null)
            {
                Logger.Write("Shortcut inspection failed: " + failure.GetType().Name +
                             " 0x" + failure.HResult.ToString("X8"));
                return;
            }

            if (!matches)
                return;

            File.SetAttributes(shortcutPath, FileAttributes.Normal);
            File.Delete(shortcutPath);
            Logger.Write("Shortcut deleted");
        }

        private static string ReadTargetCore(string shortcutPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return null;

            object shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return null;

            object link = null;
            try
            {
                link = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });

                if (link == null)
                    return null;

                object target = link.GetType().InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.GetProperty,
                    null,
                    link,
                    null);

                return target as string;
            }
            finally
            {
                try { if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
                catch { }
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                catch { }
            }
        }

        private static void CreateCore(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description)
        {
            string parent = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("Компонент WScript.Shell недоступен.");

            object shell = Activator.CreateInstance(shellType);
            if (shell == null)
                throw new InvalidOperationException("Не удалось создать WScript.Shell.");

            object link = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });

            if (link == null)
                throw new InvalidOperationException("Не удалось создать ярлык.");

            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { targetPath });
            linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { workingDirectory });
            linkType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { targetPath + ",0" });
            linkType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { description });
            linkType.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { 1 });
            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);

            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
            catch { }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            catch { }
        }
    }
}
