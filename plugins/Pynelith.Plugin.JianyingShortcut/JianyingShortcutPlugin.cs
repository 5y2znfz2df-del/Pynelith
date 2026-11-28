using System;
using System.IO;
using Pynelith.Plugins;

namespace Pynelith.Plugin.JianyingShortcut
{
    /// <summary>
    /// 剪映快捷指令（通知源，骨架）。
    ///
    /// 现在做的：启动时按 settings.executablePath 或常见安装目录找剪映，找到就推送一条就绪通知。
    /// 后续补全：找到 exe 后按剪映的命令行 / 草稿目录约定生成"一键成片"指令，
    /// 再通过 <see cref="Raise"/> 把执行结果推回岛的消息面板。
    /// </summary>
    public sealed class JianyingShortcutPlugin : INotificationSourcePlugin
    {
        private static readonly string[] CommonPaths =
        {
            @"C:\Program Files\JianyingPro\JianyingPro.exe",
            @"C:\Program Files (x86)\JianyingPro\JianyingPro.exe",
            @"D:\Program Files\JianyingPro\JianyingPro.exe",
        };

        private IPluginHost? _host;

        public event EventHandler<PluginNotification>? Received;

        public void Initialize(IPluginHost host)
        {
            _host = host;

            string configured = host.GetSetting("executablePath", string.Empty);
            string? found = FindExecutable(configured);

            if (found == null)
            {
                host.Log("未找到剪映：请在 plugin.json 的 settings.executablePath 里填写 JianyingPro.exe 的完整路径。");
                Raise("剪映未找到", "填写 plugin.json 的 executablePath 后重新导入插件即可启用快捷指令。");
                return;
            }

            host.Log("已找到剪映：" + found);
            Raise("剪映已就绪", "已检测到 " + found + "，可在 plugin.json 的 executablePath 指定其它路径。");
        }

        private static string? FindExecutable(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            foreach (string path in CommonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private void Raise(string title, string body)
        {
            Received?.Invoke(this, new PluginNotification("剪映快捷指令", title, body));
        }

        public void Dispose()
        {
            Received = null;
            _host = null;
        }
    }
}
