using System;
using System.Threading;
using System.Threading.Tasks;
using Pynelith.Plugins;

namespace Pynelith.Island.Services.Plugins
{
    /// <summary>
    /// 注入给插件的宿主上下文。
    /// 插件只通过这里读写数据，拿不到宿主窗口、服务或配置文件，保证低耦合与隔离。
    /// </summary>
    internal sealed class PluginHostContext : IPluginHost, IInteractiveHost
    {
        private readonly PluginManager _manager;
        private readonly PluginDescriptor _descriptor;

        public PluginHostContext(PluginManager manager, PluginDescriptor descriptor)
        {
            _manager = manager;
            _descriptor = descriptor;
        }

        public string PluginDirectory => _descriptor.Directory;

        public string GetSetting(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key))
            {
                return fallback;
            }

            return _descriptor.Manifest.Settings.TryGetValue(key, out string? value)
                ? value
                : fallback;
        }

        public void Log(string message)
        {
            _manager.RaisePluginLog(_descriptor.Manifest.Id, message);
        }

        public void PublishNotification(string appName, string title, string body)
        {
            _manager.RaisePluginNotification(
                _descriptor,
                new PluginNotification(appName, title, body));
        }

        public void UpdateMetric(string key, string label, string value, string unit = "")
        {
            _manager.RaisePluginMetrics(
                _descriptor,
                new[] { new PluginMetric(key, label, value, unit) });
        }

        /// <summary>带输入框的通知：宿主窗口未接入时直接返回 null，不让插件卡住。</summary>
        public Task<string?> AskUserAsync(
            string title,
            string body,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            Func<string, string, int, CancellationToken, Task<string?>>? handler = _manager.AskHandler;
            return handler == null
                ? Task.FromResult<string?>(null)
                : handler(title, body, timeoutSeconds, cancellationToken);
        }
    }
}
