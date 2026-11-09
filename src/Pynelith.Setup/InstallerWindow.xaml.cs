using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pynelith.Setup
{
    /// <summary>
    /// 安装界面：选择目录 → 校验 → 释放文件 → 创建快捷方式 → 注册卸载信息。
    /// 目录只能通过系统原生文件夹对话框选择（文本框只读，禁止手输）。
    /// </summary>
    public partial class InstallerWindow : Window
    {
        private const string ProductName = "Pynelith";
        private const string AppExeName = "Pynelith.Island.exe";

        private string _installDir;
        private bool _busy;
        private bool _installed;

        public InstallerWindow()
        {
            InitializeComponent();

            _installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);
            PathBox.Text = _installDir;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            if (_busy || _installed)
            {
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "选择 Pynelith 的安装位置",
                InitialDirectory = Directory.Exists(_installDir)
                    ? _installDir
                    : Path.GetPathRoot(_installDir) ?? "C:\\",
                Multiselect = false,
            };

            if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                return;
            }

            string picked = dialog.FolderName.TrimEnd('\\');

            // 用户选中的若是产品目录本身则直接用，否则在其下建产品目录
            if (!string.Equals(Path.GetFileName(picked), ProductName, StringComparison.OrdinalIgnoreCase))
            {
                picked = Path.Combine(picked, ProductName);
            }

            _installDir = picked;
            PathBox.Text = _installDir;
            StatusText.Text = string.Empty;
        }

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            // 已完成安装：按钮变成"启动 Pynelith"
            if (_installed)
            {
                LaunchInstalled();
                Close();
                return;
            }

            if (!TryValidatePath(out string error))
            {
                ShowError(error);
                return;
            }

            SetBusy(true);
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 0;
            StatusText.Foreground = (Brush)FindResource("InkSoft");
            StatusText.Text = "正在准备…";

            var progress = new Progress<(int Percent, string Status)>(p =>
            {
                Progress.Value = p.Percent;
                StatusText.Text = p.Status;
            });

            try
            {
                await Task.Run(() => Installer.Install(_installDir, progress));

                _installed = true;
                Progress.Value = 100;
                StatusText.Foreground = (Brush)FindResource("InkSoft");
                StatusText.Text = "安装完成。" + _installDir;
                InstallButton.Content = "启动 Pynelith";
                InstallButton.IsEnabled = true;
                BrowseButton.IsEnabled = false;
                PathBox.IsEnabled = false;
                CloseButton.Content = "关闭";
                CloseButton.IsEnabled = true;
                SubtitleText.Text = "版本 beta0.1.0 · 安装成功";
            }
            catch (Exception ex)
            {
                Progress.Visibility = Visibility.Collapsed;
                ShowError("安装失败：" + ex.Message);
                InstallButton.Content = "重试";
                InstallButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>校验目录合法性（绝对路径、非根目录、非法字符）。</summary>
        private bool TryValidatePath(out string error)
        {
            error = string.Empty;
            string path = _installDir?.Trim() ?? string.Empty;

            if (path.Length == 0)
            {
                error = "请选择一个安装位置。";
                return false;
            }

            if (!Path.IsPathRooted(path))
            {
                error = "安装位置必须是完整的绝对路径。";
                return false;
            }

            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                error = "路径中包含无效字符，请重新选择。";
                return false;
            }

            string root = (Path.GetPathRoot(path) ?? string.Empty).TrimEnd('\\');
            if (string.Equals(path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
            {
                error = "不能直接安装到磁盘根目录，请选择或新建一个子文件夹。";
                return false;
            }

            return true;
        }

        private void ShowError(string message)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            StatusText.Text = message;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            InstallButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            CloseButton.IsEnabled = !busy;
        }

        private void LaunchInstalled()
        {
            try
            {
                string exe = Path.Combine(_installDir, AppExeName);
                // 通过 explorer 启动，避免子进程继承安装程序的管理员令牌
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动失败：" + ex.Message, "Pynelith",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
