using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Pynelith.Setup
{
    /// <summary>
    /// 安装界面（WinForms 版）：选择目录 → 校验 → 释放文件 → 快捷方式 → 卸载信息 → 自动启动。
    /// 目录只能通过系统文件夹对话框选择（文本框只读）。
    /// </summary>
    internal sealed class InstallerForm : Form
    {
        private const string ProductFolder = "Pynelith";
        private const string AppExeName = "Pynelith.Island.exe";

        private static readonly Color Ink = Color.FromArgb(0x1F, 0x23, 0x28);
        private static readonly Color InkSoft = Color.FromArgb(0x6B, 0x72, 0x80);
        private static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xED);
        private static readonly Color Danger = Color.FromArgb(0xC0, 0x39, 0x2B);

        private readonly Label _subtitle;
        private readonly TextBox _pathBox;
        private readonly Button _browseButton;
        private readonly Button _installButton;
        private readonly Button _closeButton;
        private readonly LinkLabel _uninstallLink;
        private readonly Label _status;
        private readonly ProgressBar _progress;

        private string _installDir;
        private bool _busy;
        private bool _installed;

        public InstallerForm()
        {
            Text = "Pynelith 安装程序";
            ClientSize = new Size(632, 430);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(0xFA, 0xFA, 0xFC);
            Font = new Font("Segoe UI", 9F);
            Icon = Program.LoadIcon();

            _installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductFolder);

            var title = new Label
            {
                Text = "安装 " + Installer.ProductName,
                Font = new Font("Segoe UI", 19F, FontStyle.Bold),
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(30, 24),
            };

            _subtitle = new Label
            {
                Text = "版本 beta0.1.8 · 仿 iOS 灵动岛播放面板",
                ForeColor = InkSoft,
                AutoSize = true,
                Location = new Point(32, 70),
            };

            var pathLabel = new Label
            {
                Text = "安装位置",
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(32, 110),
            };

            _pathBox = new TextBox
            {
                Location = new Point(32, 132),
                Width = 448,
                ReadOnly = true,
                Text = _installDir,
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9.5F),
            };

            _browseButton = MakeButton("浏览…", 490, 130, 100, 30, primary: false);
            _browseButton.Click += OnBrowse;

            var hint = new Label
            {
                Text = "安装需要管理员权限：创建桌面/开始菜单快捷方式，并注册为登录后自动启动。",
                ForeColor = InkSoft,
                AutoSize = true,
                Location = new Point(32, 174),
            };

            _progress = new ProgressBar
            {
                Location = new Point(32, 214),
                Width = 560,
                Height = 8,
                Visible = false,
            };

            _status = new Label
            {
                Location = new Point(32, 234),
                Width = 560,
                Height = 60,
                ForeColor = InkSoft,
                Text = string.Empty,
            };

            _uninstallLink = new LinkLabel
            {
                Text = "卸载已安装版本",
                AutoSize = true,
                Location = new Point(32, 374),
                LinkColor = Accent,
                Visible = false,
            };
            _uninstallLink.LinkClicked += OnUninstall;

            _closeButton = MakeButton("关闭", 370, 362, 90, 34, primary: false);
            _closeButton.Click += (_, _) => Close();

            _installButton = MakeButton("安装", 474, 362, 110, 34, primary: true);
            _installButton.Click += OnInstall;

            Controls.AddRange(new Control[]
            {
                title, _subtitle, pathLabel, _pathBox, _browseButton, hint,
                _progress, _status, _uninstallLink, _closeButton, _installButton,
            });

            string? existing = Installer.ReadInstallLocation();
            if (!string.IsNullOrWhiteSpace(existing) && Directory.Exists(existing))
            {
                _uninstallLink.Visible = true;
            }
        }

        private static Button MakeButton(string text, int x, int y, int width, int height, bool primary)
        {
            var button = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.White,
                ForeColor = primary ? Color.White : Ink,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
            };

            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(0xE3, 0xE6, 0xEB);
            button.FlatAppearance.MouseOverBackColor = primary
                ? Color.FromArgb(0x25, 0x5B, 0xC7)
                : Color.FromArgb(0xF1, 0xF3, 0xF7);
            return button;
        }

        private void OnBrowse(object? sender, EventArgs e)
        {
            if (_busy || _installed)
            {
                return;
            }

            using var dialog = new FolderBrowserDialog
            {
                Description = "选择 Pynelith 的安装位置",
                SelectedPath = Directory.Exists(_installDir)
                    ? _installDir
                    : Path.GetPathRoot(_installDir) ?? "C:\\",
                ShowNewFolderButton = true,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK
                || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            string picked = dialog.SelectedPath.TrimEnd('\\');
            if (!string.Equals(Path.GetFileName(picked), ProductFolder, StringComparison.OrdinalIgnoreCase))
            {
                picked = Path.Combine(picked, ProductFolder);
            }

            _installDir = picked;
            _pathBox.Text = picked;
            _status.Text = string.Empty;
        }

        private async void OnInstall(object? sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            if (_installed)
            {
                _ = Task.Run(LaunchInstalled);
                Close();
                return;
            }

            if (!TryValidatePath(out string error))
            {
                ShowError(error);
                return;
            }

            _busy = true;
            _installButton.Enabled = false;
            _browseButton.Enabled = false;
            _closeButton.Enabled = false;

            _progress.Visible = true;
            _progress.Value = 0;
            _status.ForeColor = InkSoft;
            _status.Text = "正在准备…";

            var progress = new Progress<(int Percent, string Status)>(p =>
            {
                _progress.Value = Math.Clamp(p.Percent, 0, 100);
                _status.Text = p.Status;
            });

            try
            {
                await Task.Run(() => Installer.Install(_installDir, progress));

                _installed = true;
                _progress.Value = 100;
                _status.ForeColor = InkSoft;
                _status.Text = "安装完成，正在启动灵动岛…";
                _installButton.Text = "启动 Pynelith";
                _pathBox.Enabled = false;
                _uninstallLink.Visible = true;
                _subtitle.Text = "版本 beta0.1.8 · 安装成功";

                bool started = await Task.Run(LaunchInstalled);
                _status.Text = started
                    ? "安装完成，灵动岛已启动。\r\n" + _installDir
                    : "安装完成。若未看到灵动岛，请点击「启动 Pynelith」。\r\n" + _installDir;
            }
            catch (Exception ex)
            {
                _progress.Visible = false;
                ShowError("安装失败：" + ex.Message);
                _installButton.Text = "重试";
            }
            finally
            {
                _busy = false;
                _installButton.Enabled = true;
                _closeButton.Enabled = true;
                _browseButton.Enabled = !_installed;
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
            _status.ForeColor = Danger;
            _status.Text = message;
        }

        /// <summary>
        /// 启动已安装的程序：优先经 explorer 触发（避免继承管理员令牌），
        /// 3 秒内没观察到进程就改为直接启动兜底。返回是否成功拉起。
        /// </summary>
        private bool LaunchInstalled()
        {
            string exe = Path.Combine(_installDir, AppExeName);
            if (!File.Exists(exe))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // 交给下面的兜底
            }

            for (int attempt = 0; attempt < 15; attempt++)
            {
                Thread.Sleep(200);
                if (Process.GetProcessesByName("Pynelith.Island").Length > 0)
                {
                    return true;
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = _installDir,
                });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>一键卸载：调用安装目录里的卸载程序，连同配置、缓存与自启项一起清除。</summary>
        private void OnUninstall(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string? dir = Installer.ReadInstallLocation();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "未找到已安装的 Pynelith。", "卸载 Pynelith",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string uninstaller = Path.Combine(dir, Installer.UninstallExeName);
            if (!File.Exists(uninstaller))
            {
                MessageBox.Show(this,
                    "卸载程序缺失，可手动删除安装目录：\r\n" + dir,
                    "卸载 Pynelith", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                "将卸载 Pynelith，并清除程序、快捷方式、开机自启项、配置与歌词缓存。\r\n\r\n继续吗？",
                "卸载 Pynelith",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                // /quiet：这里已经确认过，卸载器不再二次弹窗
                Process.Start(new ProcessStartInfo(uninstaller, "/uninstall /quiet")
                {
                    UseShellExecute = true,
                });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法启动卸载程序：" + ex.Message, "Pynelith",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
