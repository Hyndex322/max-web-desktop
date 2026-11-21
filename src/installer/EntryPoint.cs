using System;
using System.Windows.Forms;

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
            Logger.Write("Options: " + options.SafeSummary());

            try
            {
                if (!string.IsNullOrEmpty(options.CleanupDir))
                {
                    int code = InstallerEngine.CleanupDirectory(
                        options.CleanupDir, options.CleanupId);
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
                        string profileDir = SetupOptions.ResolveProfileDir(options.ProfileDir);
                        InstallerEngine.Uninstall(
                            dir, !options.DeleteData, profileDir, null);
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

                        if (InstallerEngine.HasForeignFiles(dir))
                            dir = InstallerEngine.NormalizeInstallDir(
                                System.IO.Path.Combine(dir, "MAX"));
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Invalid silent install path: " +
                                     ex.GetType().Name + " 0x" +
                                     ex.HResult.ToString("X8"));
                        return 2;
                    }

                    try
                    {
                        InstallerEngine.Install(dir, !options.NoDesktop, false, null);
                        return 0;
                    }
                    catch (WebView2InstallException ex)
                    {
                        Logger.Write("WebView2 is required for silent install: " + ex.Message);
                        return 4;
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Silent install failed: " + ex.GetType().Name +
                                     " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                        return 2;
                    }
                }

                Application.Run(new InstallerForm(options));
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Write("FATAL: " + ex.GetType().Name +
                             " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
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
}
