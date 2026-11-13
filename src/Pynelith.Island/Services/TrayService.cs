using System;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 系统托盘常驻图标。岛本身没有关闭按钮，托盘提供固定的设置与退出入口，
    /// 这样即使岛被别的窗口挡住，也能找到程序。
    /// </summary>
    internal sealed class TrayService : IDisposable
    {
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Drawing.Icon _icon;
        private bool _disposed;

        public TrayService(Action openSettings, Action exit)
        {
            _icon = LoadIcon();

            var menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false,
            };

            var settingsItem = new Forms.ToolStripMenuItem("设置…");
            settingsItem.Click += (_, _) => openSettings();

            var exitItem = new Forms.ToolStripMenuItem("退出 Pynelith");
            exitItem.Click += (_, _) => exit();

            menu.Items.Add(settingsItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _icon,
                Text = "Pynelith · 灵动岛",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _notifyIcon.DoubleClick += (_, _) => openSettings();
        }

        /// <summary>从嵌入资源里按托盘尺寸挑一帧，避免 32px 硬缩到 16px 发糊。</summary>
        private static Drawing.Icon LoadIcon()
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Pynelith.Island;component/Assets/pynelith.ico"));
                if (resource != null)
                {
                    return new Drawing.Icon(resource.Stream, Forms.SystemInformation.SmallIconSize);
                }
            }
            catch (Exception)
            {
                // 资源缺失时退回系统图标，托盘本身不能因此启动失败。
            }

            return Drawing.SystemIcons.Application;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _icon.Dispose();
        }
    }
}
