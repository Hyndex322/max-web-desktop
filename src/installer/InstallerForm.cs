using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MaxSetup
{
    internal sealed class InstallerForm : Form
    {
        private readonly SetupOptions _options;
        private readonly bool _uninstallMode;
        private readonly string _installDir;

        private bool _busy;
        private bool _initializing;
        private Bitmap _logo;

        private Panel _header;
        private ComboBox _driveBox;
        private TextBox _pathBox;
        private Button _browseButton;
        private CheckBox _desktopCheck;
        private CheckBox _launchCheck;
        private CheckBox _deleteDataCheck;
        private ProgressBar _progress;
        private Label _statusLabel;
        private Button _actionButton;
        private Button _cancelButton;

        public InstallerForm(SetupOptions options)
        {
            _options = options;
            _uninstallMode = options.Uninstall;
            _installDir = options.Dir ?? InstallerEngine.GetInstalledDir();

            Text = _uninstallMode ? "Удаление MAX" : "Установка MAX";
            Font = new Font("Segoe UI", 9.5F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 430);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 250, 253);

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
        }

        private void BuildUi()
        {
            BuildHeader();

            if (_uninstallMode)
                BuildUninstallControls();
            else
                BuildInstallControls();

            _progress = new ProgressBar();
            _progress.Location = new Point(24, 300);
            _progress.Size = new Size(612, 10);
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            _progress.Style = ProgressBarStyle.Continuous;
            Controls.Add(_progress);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(24, 316);
            _statusLabel.Size = new Size(612, 22);
            _statusLabel.AutoEllipsis = true;
            _statusLabel.ForeColor = Color.FromArgb(90, 90, 105);
            _statusLabel.Text = _uninstallMode ? "Готово к удалению." : "Готово к установке.";
            Controls.Add(_statusLabel);

            _cancelButton = new Button();
            _cancelButton.Text = "Отмена";
            _cancelButton.Location = new Point(330, 366);
            _cancelButton.Size = new Size(104, 40);
            _cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancelButton);
            CancelButton = _cancelButton;

            _actionButton = new Button();
            _actionButton.Location = new Point(444, 366);
            _actionButton.Size = new Size(192, 40);
            _actionButton.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            _actionButton.BackColor = Color.FromArgb(104, 78, 235);
            _actionButton.ForeColor = Color.White;
            _actionButton.FlatStyle = FlatStyle.Flat;
            _actionButton.FlatAppearance.BorderSize = 0;
            _actionButton.UseVisualStyleBackColor = false;
            _actionButton.Click += ActionButton_Click;

            if (_uninstallMode)
            {
                _actionButton.Text = "Удалить";
                _actionButton.BackColor = Color.FromArgb(200, 55, 70);
                if (string.IsNullOrEmpty(_installDir) || !Directory.Exists(_installDir))
                {
                    _actionButton.Enabled = false;
                    _statusLabel.Text = "Установленный MAX не найден.";
                    _statusLabel.ForeColor = Color.FromArgb(190, 45, 60);
                }
            }
            else
            {
                _actionButton.Text = InstallerEngine.GetInstalledDir() != null ? "Обновить" : "Установить";
            }
            Controls.Add(_actionButton);

            var footer = new Label();
            footer.Text = "Версия " + InstallerEngine.Version;
            footer.Location = new Point(24, 378);
            footer.AutoSize = true;
            footer.ForeColor = Color.FromArgb(150, 150, 165);
            footer.Font = new Font("Segoe UI", 8.25F);
            Controls.Add(footer);
        }

        private void BuildHeader()
        {
            _header = new GradientPanel();
            _header.Dock = DockStyle.Top;
            _header.Height = 96;
            _header.BackColor = Color.FromArgb(104, 78, 235);
            _header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var brush = new LinearGradientBrush(
                    _header.ClientRectangle,
                    Color.FromArgb(140, 112, 255),
                    Color.FromArgb(74, 44, 190),
                    0f))
                {
                    e.Graphics.FillRectangle(brush, _header.ClientRectangle);
                }
            };
            Controls.Add(_header);

            _logo = Branding.CreateLogo(48);
            var logo = new PictureBox();
            logo.Image = _logo;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(24, 24);
            logo.Size = new Size(48, 48);
            logo.BackColor = Color.Transparent;
            _header.Controls.Add(logo);

            var title = new Label();
            title.Text = _uninstallMode ? "Удаление MAX" : "Установка MAX";
            title.Font = new Font("Segoe UI", 20F, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.BackColor = Color.Transparent;
            title.AutoSize = true;
            title.Location = new Point(88, 17);
            _header.Controls.Add(title);

            var subtitle = new Label();
            subtitle.Text = _uninstallMode
                ? "Удаление мессенджера с компьютера"
                : "Мессенджер MAX для Windows";
            subtitle.Font = new Font("Segoe UI", 9.5F);
            subtitle.ForeColor = Color.FromArgb(225, 220, 255);
            subtitle.BackColor = Color.Transparent;
            subtitle.AutoSize = true;
            subtitle.Location = new Point(91, 57);
            _header.Controls.Add(subtitle);
        }

        private void BuildInstallControls()
        {
            var folderLabel = new Label();
            folderLabel.Text = "Папка установки:";
            folderLabel.Location = new Point(24, 114);
            folderLabel.AutoSize = true;
            folderLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            Controls.Add(folderLabel);

            _driveBox = new ComboBox();
            _driveBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _driveBox.Location = new Point(24, 138);
            _driveBox.Size = new Size(100, 26);
            _driveBox.SelectedIndexChanged += DriveBox_SelectedIndexChanged;
            Controls.Add(_driveBox);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(132, 138);
            _pathBox.Size = new Size(390, 26);
            Controls.Add(_pathBox);

            _browseButton = new Button();
            _browseButton.Text = "Обзор...";
            _browseButton.Location = new Point(530, 136);
            _browseButton.Size = new Size(106, 30);
            _browseButton.Click += BrowseButton_Click;
            Controls.Add(_browseButton);

            _desktopCheck = new CheckBox();
            _desktopCheck.Text = "Создать ярлык MAX на рабочем столе";
            _desktopCheck.Location = new Point(24, 186);
            _desktopCheck.AutoSize = true;
            _desktopCheck.Checked = !_options.NoDesktop;
            Controls.Add(_desktopCheck);

            _launchCheck = new CheckBox();
            _launchCheck.Text = "Запустить MAX после установки";
            _launchCheck.Location = new Point(24, 212);
            _launchCheck.AutoSize = true;
            _launchCheck.Checked = !_options.NoLaunch;
            Controls.Add(_launchCheck);

            var info = new Label();
            info.Text =
                "Файлы будут установлены в папку Max на выбранном диске. " +
                "Если для записи нужны права администратора, установщик предложит их получить.";
            info.Location = new Point(24, 248);
            info.Size = new Size(612, 40);
            info.ForeColor = Color.FromArgb(105, 105, 120);
            Controls.Add(info);

            PopulateDrives();
        }

        private void BuildUninstallControls()
        {
            var caption = new Label();
            caption.Text = "MAX установлен в папку:";
            caption.Location = new Point(24, 114);
            caption.AutoSize = true;
            caption.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            Controls.Add(caption);

            var path = new Label();
            path.Text = string.IsNullOrEmpty(_installDir)
                ? "не найдена"
                : _installDir;
            path.Location = new Point(24, 140);
            path.Size = new Size(612, 24);
            path.AutoEllipsis = true;
            path.Font = new Font("Segoe UI", 10F);
            path.ForeColor = Color.FromArgb(60, 60, 75);
            Controls.Add(path);

            _deleteDataCheck = new CheckBox();
            _deleteDataCheck.Text = "Удалить также сохранённую сессию и настройки";
            _deleteDataCheck.Location = new Point(24, 182);
            _deleteDataCheck.AutoSize = true;
            _deleteDataCheck.Checked = !_options.KeepData;
            Controls.Add(_deleteDataCheck);

            var info = new Label();
            info.Text =
                "Будут удалены файлы программы, ярлыки на рабочем столе и в меню «Пуск», " +
                "а также запись в списке установленных программ Windows.";
            info.Location = new Point(24, 216);
            info.Size = new Size(612, 44);
            info.ForeColor = Color.FromArgb(105, 105, 120);
            Controls.Add(info);
        }

        private void PopulateDrives()
        {
            _initializing = true;
            try
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                string initialPath = _options.Dir;

                if (string.IsNullOrEmpty(initialPath))
                {
                    string installed = InstallerEngine.GetInstalledDir();
                    initialPath = installed ?? Path.Combine(systemRoot, "Max");
                }

                try
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        if (!drive.IsReady) continue;
                        if (drive.DriveType != DriveType.Fixed &&
                            drive.DriveType != DriveType.Removable &&
                            drive.DriveType != DriveType.Network)
                            continue;

                        string label = string.IsNullOrEmpty(drive.VolumeLabel)
                            ? string.Empty
                            : " (" + drive.VolumeLabel + ")";

                        _driveBox.Items.Add(new DriveItem
                        {
                            Root = drive.RootDirectory.FullName,
                            Display = drive.Name + label
                        });
                    }
                }
                catch { }

                string pathRoot = Path.GetPathRoot(initialPath);
                bool selected = false;

                foreach (object item in _driveBox.Items)
                {
                    var driveItem = item as DriveItem;
                    if (driveItem == null) continue;
                    if (string.Equals(
                        driveItem.Root.TrimEnd('\\'),
                        (pathRoot ?? string.Empty).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        _driveBox.SelectedItem = driveItem;
                        selected = true;
                        break;
                    }
                }

                if (!selected && !string.IsNullOrEmpty(pathRoot))
                {
                    var item = new DriveItem { Root = pathRoot, Display = pathRoot };
                    _driveBox.Items.Add(item);
                    _driveBox.SelectedItem = item;
                }

                _pathBox.Text = initialPath;
            }
            finally
            {
                _initializing = false;
            }
        }

        private void DriveBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_initializing) return;

            var item = _driveBox.SelectedItem as DriveItem;
            if (item == null) return;

            _pathBox.Text = Path.Combine(item.Root, "Max");
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для установки MAX";
                dialog.ShowNewFolderButton = true;
                try { dialog.SelectedPath = InstallerEngine.NormalizeInstallDir(_pathBox.Text); }
                catch { }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string selected = dialog.SelectedPath;
                string root = Path.GetPathRoot(selected);
                if (!string.IsNullOrEmpty(root) &&
                    string.Equals(selected.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    selected = Path.Combine(root, "Max");
                }

                _pathBox.Text = selected;
            }
        }

        private async void ActionButton_Click(object sender, EventArgs e)
        {
            if (_busy) return;

            if (_uninstallMode)
                await RunUninstallAsync();
            else
                await RunInstallAsync();
        }

        private async Task RunInstallAsync()
        {
            string dir;
            try
            {
                dir = InstallerEngine.NormalizeInstallDir(_pathBox.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "MAX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _pathBox.Text = dir;

            if (InstallerEngine.IsAppRunning())
            {
                DialogResult answer = MessageBox.Show(
                    this,
                    "MAX сейчас запущен. Закрыть его и продолжить установку?",
                    "MAX",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes)
                    return;
            }

            SetBusy(true);
            _statusLabel.Text = "Подготовка...";
            var progress = new Progress<InstallProgress>(UpdateProgress);

            try
            {
                await Task.Run(delegate
                {
                    InstallerEngine.Install(dir, _desktopCheck.Checked, progress);
                });

                if (_launchCheck.Checked)
                    InstallerEngine.Launch(dir);

                string message = "MAX успешно установлен.\n\nПапка: " + dir;
                if (_desktopCheck.Checked)
                {
                    string desktopLink = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "MAX.lnk");
                    message += File.Exists(desktopLink)
                        ? "\n\nЯрлык MAX добавлен на рабочий стол."
                        : "\n\nНе удалось создать ярлык на рабочем столе.";
                }

                MessageBox.Show(this, message, "MAX", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                Logger.Write("Access denied during install: " + ex);
                HandleElevation(dir);
            }
            catch (Exception ex)
            {
                Logger.Write("Install failed: " + ex);
                MessageBox.Show(
                    this,
                    "Не удалось установить MAX.\n\n" + ex.Message +
                    "\n\nЖурнал: " + Logger.FilePath,
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
        }

        private async Task RunUninstallAsync()
        {
            string dir = _installDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(
                    this,
                    "Установленный MAX не найден.",
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "Удалить MAX с этого компьютера?",
                "MAX",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
                return;

            bool deleteData = _deleteDataCheck.Checked;
            SetBusy(true);
            _statusLabel.Text = "Удаление...";
            var progress = new Progress<InstallProgress>(UpdateProgress);

            try
            {
                await Task.Run(delegate
                {
                    InstallerEngine.Uninstall(dir, !deleteData, progress);
                });

                MessageBox.Show(
                    this,
                    "MAX удалён с компьютера." +
                    (deleteData ? string.Empty : "\n\nДанные входа и настройки сохранены."),
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                Close();
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                Logger.Write("Access denied during uninstall: " + ex);
                HandleUninstallElevation(dir, deleteData);
            }
            catch (Exception ex)
            {
                Logger.Write("Uninstall failed: " + ex);
                MessageBox.Show(
                    this,
                    "Не удалось полностью удалить MAX.\n\n" + ex.Message +
                    "\n\nЖурнал: " + Logger.FilePath,
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
        }

        private void HandleElevation(string dir)
        {
            if (InstallerEngine.IsElevated())
            {
                MessageBox.Show(
                    this,
                    "Не удалось получить доступ к папке:\n" + dir +
                    "\n\nВыберите другую папку или запустите установщик от имени администратора.",
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "Для записи в папку:\n" + dir +
                "\n\nнужны права администратора. Перезапустить установщик от имени администратора?",
                "MAX",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
            {
                _statusLabel.Text = "Выберите другую папку, например на другом диске.";
                return;
            }

            string args = "/dir=\"" + dir + "\"";
            if (!_desktopCheck.Checked) args += " /nodesktop";
            if (!_launchCheck.Checked) args += " /nolaunch";

            if (InstallerEngine.RelaunchElevated(args))
                Close();
        }

        private void HandleUninstallElevation(string dir, bool deleteData)
        {
            if (InstallerEngine.IsElevated())
            {
                MessageBox.Show(
                    this,
                    "Не удалось удалить файлы из папки:\n" + dir +
                    "\n\nЗапустите удаление от имени администратора.",
                    "MAX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "Для удаления MAX нужны права администратора. Перезапустить удаление от имени администратора?",
                "MAX",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
                return;

            string args = "/uninstall /dir=\"" + dir + "\"";
            if (!deleteData) args += " /keepdata";

            if (InstallerEngine.RelaunchElevated(args))
                Close();
        }

        private static bool IsAccessDenied(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return true;

            var io = ex as IOException;
            if (io != null && (io.HResult & 0xFFFF) == 5) return true;

            string message = (ex.Message ?? string.Empty).ToLowerInvariant();
            return message.Contains("отказано") || message.Contains("denied");
        }

        private void UpdateProgress(InstallProgress progress)
        {
            if (progress == null) return;

            if (!string.IsNullOrEmpty(progress.Text))
                _statusLabel.Text = progress.Text;

            int value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, progress.Percent));
            _progress.Value = value;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _actionButton.Enabled = !busy;
            _cancelButton.Enabled = !busy;

            if (_pathBox != null) _pathBox.Enabled = !busy;
            if (_driveBox != null) _driveBox.Enabled = !busy;
            if (_browseButton != null) _browseButton.Enabled = !busy;
            if (_desktopCheck != null) _desktopCheck.Enabled = !busy;
            if (_launchCheck != null) _launchCheck.Enabled = !busy;
            if (_deleteDataCheck != null) _deleteDataCheck.Enabled = !busy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _logo != null)
            {
                _logo.Dispose();
                _logo = null;
            }
            base.Dispose(disposing);
        }

        private sealed class GradientPanel : Panel
        {
            public GradientPanel()
            {
                ResizeRedraw = true;
            }
        }

        private sealed class DriveItem
        {
            public string Root;
            public string Display;

            public override string ToString()
            {
                return Display ?? Root ?? string.Empty;
            }
        }
    }
}
