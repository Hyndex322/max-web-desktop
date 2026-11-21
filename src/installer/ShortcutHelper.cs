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
                Logger.Write("Shortcut creation failed for " + shortcutPath + ": " + failure.Message);
                throw new IOException("Не удалось создать ярлык: " + shortcutPath, failure);
            }

            if (!File.Exists(shortcutPath))
                throw new IOException("Ярлык не был создан: " + shortcutPath);

            Logger.Write("Shortcut created: " + shortcutPath);
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
