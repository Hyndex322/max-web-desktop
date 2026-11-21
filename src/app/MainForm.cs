using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MaxMessenger
{
    public sealed class MainForm : Form
    {
        private readonly bool _selfTest;
        private readonly string _selfTestResultPath;
        private readonly WebView2 _webView;
        private readonly Panel _errorPanel;
        private readonly Label _errorTitle;
        private readonly Label _errorDetails;
        private readonly Button _retryButton;
        private readonly FlowLayoutPanel _errorContent;
        private readonly Timer _selfTestTimer;
        private readonly AppSettings _settings;

        private bool _coreReady;
        private bool _selfTestDone;
        private bool _fullScreen;
        private FormBorderStyle _oldBorderStyle;
        private FormWindowState _oldWindowState;
        private int _rendererRestarts;

        public int ExitCode { get; private set; }

        public MainForm(bool selfTest, string selfTestResultPath)
        {
            _selfTest = selfTest;
            _selfTestResultPath = selfTestResultPath;

            Text = AppInfo.WindowTitle;
            MinimumSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 32);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _settings = AppSettings.Load();
            ApplySavedBounds();

            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            _webView.DefaultBackgroundColor = Color.FromArgb(24, 24, 32);
            Controls.Add(_webView);

            _errorTitle = new Label();
            _errorTitle.AutoSize = true;
            _errorTitle.Font = new Font("Segoe UI", 17F, FontStyle.Bold);
            _errorTitle.ForeColor = Color.White;
            _errorTitle.Margin = new Padding(0, 0, 0, 10);

            _errorDetails = new Label();
            _errorDetails.AutoSize = true;
            _errorDetails.MaximumSize = new Size(560, 0);
            _errorDetails.Font = new Font("Segoe UI", 10F);
            _errorDetails.ForeColor = Color.FromArgb(190, 190, 205);
            _errorDetails.Margin = new Padding(0, 0, 0, 22);

            _retryButton = new Button();
            _retryButton.Text = "Повторить";
            _retryButton.Size = new Size(150, 38);
            _retryButton.Margin = new Padding(0);
            _retryButton.Click += delegate { Retry(); };

            _errorContent = new FlowLayoutPanel();
            _errorContent.FlowDirection = FlowDirection.TopDown;
            _errorContent.WrapContents = false;
            _errorContent.AutoSize = true;
            _errorContent.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _errorContent.Anchor = AnchorStyles.None;
            _errorContent.Controls.Add(_errorTitle);
            _errorContent.Controls.Add(_errorDetails);
            _errorContent.Controls.Add(_retryButton);

            _errorPanel = new Panel();
            _errorPanel.Dock = DockStyle.Fill;
            _errorPanel.BackColor = Color.FromArgb(24, 24, 32);
            _errorPanel.Visible = false;
            _errorPanel.Controls.Add(_errorContent);
            _errorPanel.Resize += delegate { CenterErrorContent(); };
            Controls.Add(_errorPanel);
            _errorPanel.BringToFront();

            _selfTestTimer = new Timer();
            _selfTestTimer.Interval = 90000;
            _selfTestTimer.Tick += delegate
            {
                _selfTestTimer.Stop();
                FinishSelfTest(false, "timeout waiting for page load");
            };

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnableDarkTitleBar();
        }

        private void EnableDarkTitleBar()
        {
            try
            {
                int enabled = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+),
                // 19 = same attribute on earlier Windows 10 builds.
                int result = NativeMethods.DwmSetWindowAttribute(
                    Handle, 20, ref enabled, sizeof(int));

                if (result != 0)
                    NativeMethods.DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
            }
            catch
            {
                // Cosmetic only.
            }
        }

        private void ApplySavedBounds()
        {
            if (_selfTest)
            {
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(1024, 768);
                return;
            }

            if (_settings.Width >= MinimumSize.Width && _settings.Height >= MinimumSize.Height)
                Size = new Size(_settings.Width, _settings.Height);

            if (_settings.X != int.MinValue && _settings.Y != int.MinValue)
            {
                var bounds = new Rectangle(_settings.X, _settings.Y, Width, Height);
                if (IsVisibleOnAnyScreen(bounds))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = bounds;
                }
            }

            if (_settings.Maximized)
                WindowState = FormWindowState.Maximized;
        }

        private static bool IsVisibleOnAnyScreen(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                if (Rectangle.Intersect(bounds, screen.WorkingArea).Width >= 200 &&
                    Rectangle.Intersect(bounds, screen.WorkingArea).Height >= 100)
                    return true;
            }
            return false;
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                string runtimeVersion;
                try
                {
                    runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                }
                catch (Exception ex)
                {
                    Logger.Error("WebView2 runtime is not available", ex);
                    ShowRuntimeMissing();
                    return;
                }

                Logger.Info("WebView2 Runtime version: " + runtimeVersion);

                Directory.CreateDirectory(AppPaths.UserDataDir);
                var environment = await CoreWebView2Environment.CreateAsync(
                    null,
                    AppPaths.UserDataDir,
                    null);

                await _webView.EnsureCoreWebView2Async(environment);

                ConfigureCoreWebView2(_webView.CoreWebView2);
                _coreReady = true;

                if (_selfTest)
                    _selfTestTimer.Start();

                _webView.CoreWebView2.Navigate(AppInfo.StartUrl);
                Logger.Info("Navigating to " + AppInfo.StartUrl);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize WebView2", ex);
                ShowError("Не удалось запустить MAX", ex.Message);
                FinishSelfTest(false, "init failed: " + ex.Message);
            }
        }

        private void ConfigureCoreWebView2(CoreWebView2 core)
        {
            CoreWebView2Settings settings = core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreHostObjectsAllowed = false;

            core.NewWindowRequested += Core_NewWindowRequested;
            core.PermissionRequested += Core_PermissionRequested;
            core.ProcessFailed += Core_ProcessFailed;
            core.DownloadStarting += Core_DownloadStarting;
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.ContainsFullScreenElementChanged += Core_ContainsFullScreenElementChanged;
        }

        private void Core_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;

            string uri = e.Uri;
            if (string.IsNullOrEmpty(uri))
                return;

            if (IsTrustedUri(uri))
            {
                try { _webView.CoreWebView2.Navigate(uri); }
                catch (Exception ex) { Logger.Error("Navigation failed: " + uri, ex); }
                return;
            }

            OpenExternal(uri);
        }

        private static void OpenExternal(string uri)
        {
            try
            {
                var parsed = new Uri(uri);
                if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                    return;

                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to open external link: " + uri, ex);
            }
        }

        private void Core_PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            // Allow camera, microphone, notifications, clipboard and geolocation
            // only for MAX itself. Everything else is denied.
            e.State = IsTrustedUri(e.Uri)
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        }

        private void Core_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Logger.Error("WebView2 process failed: " + e.ProcessFailedKind, null);

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                ShowError(
                    "Работа MAX была прервана",
                    "Внутренний процесс браузера завершился. Закройте и снова откройте приложение.");
                return;
            }

            if (_rendererRestarts < 2)
            {
                _rendererRestarts++;
                try
                {
                    _webView.Reload();
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("Reload after process failure failed", ex);
                }
            }

            ShowError(
                "Что-то пошло не так",
                "Не удалось восстановить работу страницы. Перезапустите приложение.");
        }

        private void Core_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            Logger.Info("Download started: " + e.DownloadOperation.Uri + " -> " + e.ResultFilePath);
        }

        private void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            HideError();
        }

        private void Core_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                HideError();
                _rendererRestarts = 0;
                FinishSelfTest(true, _webView.CoreWebView2.Source);
                return;
            }

            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                return;

            string message = DescribeError(e.WebErrorStatus);
            Logger.Error("Navigation failed: " + e.WebErrorStatus, null);
            ShowError("Не удалось загрузить MAX", message);
            FinishSelfTest(false, e.WebErrorStatus.ToString());
        }

        private void Core_ContainsFullScreenElementChanged(object sender, object e)
        {
            bool contains;
            try { contains = _webView.CoreWebView2.ContainsFullScreenElement; }
            catch { return; }

            SetFullScreen(contains);
        }

        private void SetFullScreen(bool enabled)
        {
            if (_fullScreen == enabled)
                return;

            _fullScreen = enabled;

            if (enabled)
            {
                _oldBorderStyle = FormBorderStyle;
                _oldWindowState = WindowState;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = _oldBorderStyle;
                WindowState = _oldWindowState;
            }
        }

        private void ShowRuntimeMissing()
        {
            if (_selfTest)
            {
                FinishSelfTest(false, "WebView2 runtime is not installed");
                return;
            }

            DialogResult result = MessageBox.Show(
                "Для работы MAX требуется компонент Microsoft Edge WebView2, " +
                "но он не установлен.\n\n" +
                "Открыть страницу загрузки компонента?",
                "MAX",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
                OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");

            BeginInvoke(new Action(Close));
        }

        private void ShowError(string title, string details)
        {
            _errorTitle.Text = title;
            _errorDetails.Text = details;
            _errorPanel.Visible = true;
            _errorPanel.BringToFront();
            CenterErrorContent();
        }

        private void HideError()
        {
            _errorPanel.Visible = false;
        }

        private void CenterErrorContent()
        {
            int left = Math.Max(0, (_errorPanel.ClientSize.Width - _errorContent.Width) / 2);
            int top = Math.Max(0, (_errorPanel.ClientSize.Height - _errorContent.Height) / 2);
            _errorContent.Location = new Point(left, top);
        }

        private async void Retry()
        {
            HideError();

            if (!_coreReady)
            {
                try
                {
                    var environment = await CoreWebView2Environment.CreateAsync(
                        null,
                        AppPaths.UserDataDir,
                        null);
                    await _webView.EnsureCoreWebView2Async(environment);
                    ConfigureCoreWebView2(_webView.CoreWebView2);
                    _coreReady = true;
                }
                catch (Exception ex)
                {
                    Logger.Error("Retry initialization failed", ex);
                    ShowError("Не удалось запустить MAX", ex.Message);
                    return;
                }
            }

            try
            {
                _webView.CoreWebView2.Navigate(AppInfo.StartUrl);
            }
            catch (Exception ex)
            {
                Logger.Error("Retry navigation failed", ex);
                ShowError("Не удалось загрузить MAX", ex.Message);
            }
        }

        private static bool IsTrustedUri(string uri)
        {
            try
            {
                var parsed = new Uri(uri);
                string host = parsed.Host.ToLowerInvariant();
                return host == "max.ru" || host.EndsWith(".max.ru", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeError(CoreWebView2WebErrorStatus status)
        {
            switch (status)
            {
                case CoreWebView2WebErrorStatus.Disconnected:
                case CoreWebView2WebErrorStatus.ServerUnreachable:
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                    return "Похоже, нет подключения к интернету. Проверьте сеть и нажмите «Повторить».";
                case CoreWebView2WebErrorStatus.Timeout:
                    return "Сервер не ответил вовремя. Проверьте подключение и нажмите «Повторить».";
                case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset:
                    return "Соединение было прервано. Нажмите «Повторить».";
                case CoreWebView2WebErrorStatus.CannotConnect:
                    return "Не удалось подключиться к серверу. Проверьте подключение к интернету.";
                case CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect:
                case CoreWebView2WebErrorStatus.CertificateExpired:
                case CoreWebView2WebErrorStatus.CertificateIsInvalid:
                    return "Проблема с сертификатом безопасности. Проверьте дату и время на компьютере.";
                default:
                    return "Не удалось открыть страницу (" + status + "). Нажмите «Повторить».";
            }
        }

        private void FinishSelfTest(bool success, string details)
        {
            if (!_selfTest || _selfTestDone)
                return;

            _selfTestDone = true;
            _selfTestTimer.Stop();
            ExitCode = success ? 0 : 1;

            try
            {
                if (_selfTestResultPath != null)
                {
                    File.WriteAllText(
                        _selfTestResultPath,
                        (success ? "OK" : "FAIL") + Environment.NewLine +
                        (details ?? string.Empty) + Environment.NewLine +
                        "userData=" + AppPaths.UserDataDir + Environment.NewLine +
                        "userDataExists=" + Directory.Exists(AppPaths.UserDataDir) + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to write self-test result", ex);
            }

            Logger.Info("Self-test: " + (success ? "OK" : "FAIL") + " " + details);

            try { BeginInvoke(new Action(Close)); }
            catch { }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                if (!_selfTest)
                {
                    Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    if (bounds.Width >= MinimumSize.Width && bounds.Height >= MinimumSize.Height)
                    {
                        _settings.X = bounds.X;
                        _settings.Y = bounds.Y;
                        _settings.Width = bounds.Width;
                        _settings.Height = bounds.Height;
                    }
                    _settings.Maximized = !_fullScreen && WindowState == FormWindowState.Maximized;
                    _settings.Save();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to persist window state", ex);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5 || keyData == (Keys.Control | Keys.R))
            {
                if (_coreReady) _webView.Reload();
                return true;
            }

            if (keyData == Keys.F11)
            {
                SetFullScreen(!_fullScreen);
                return true;
            }

            if (keyData == Keys.Escape && _fullScreen)
            {
                SetFullScreen(false);
                return true;
            }

            if (keyData == (Keys.Alt | Keys.Left))
            {
                if (_coreReady && _webView.CoreWebView2.CanGoBack)
                    _webView.CoreWebView2.GoBack();
                return true;
            }

            if (keyData == (Keys.Alt | Keys.Right))
            {
                if (_coreReady && _webView.CoreWebView2.CanGoForward)
                    _webView.CoreWebView2.GoForward();
                return true;
            }

            if (keyData == (Keys.Control | Keys.D0))
            {
                if (_coreReady) _webView.ZoomFactor = 1.0;
                return true;
            }

            if (keyData == (Keys.Control | Keys.Oemplus) ||
                keyData == (Keys.Control | Keys.Add))
            {
                if (_coreReady) _webView.ZoomFactor = Math.Min(3.0, _webView.ZoomFactor + 0.1);
                return true;
            }

            if (keyData == (Keys.Control | Keys.OemMinus) ||
                keyData == (Keys.Control | Keys.Subtract))
            {
                if (_coreReady) _webView.ZoomFactor = Math.Max(0.5, _webView.ZoomFactor - 0.1);
                return true;
            }

            // Developer tools are intentionally disabled.
            if (keyData == (Keys.Control | Keys.Shift | Keys.I) ||
                keyData == Keys.F12)
                return true;

            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
