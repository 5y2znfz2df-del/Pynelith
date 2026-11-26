using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Plugins
{
    /// <summary>
    /// 所有插件的基接口。宿主创建实例后立即调用 <see cref="Initialize"/>。
    /// 契约只增不改：新增接口成员一律带默认实现或放到新接口里，保证老插件继续可用。
    /// </summary>
    public interface IPynelithPlugin : IDisposable
    {
        /// <summary>宿主注入的上下文，插件在此读取配置、写日志、推送数据。</summary>
        void Initialize(IPluginHost host);
    }

    /// <summary>
    /// 宿主提供给插件的上下文。所有成员都必须是线程安全的。
    /// </summary>
    public interface IPluginHost
    {
        /// <summary>插件自身所在的目录（可读写，用于缓存）。</summary>
        string PluginDirectory { get; }

        /// <summary>读取 plugin.json 里 settings 段的配置项。</summary>
        string GetSetting(string key, string fallback);

        /// <summary>写日志，宿主的日志文件会带上插件 id。</summary>
        void Log(string message);

        /// <summary>向灵动岛推送一条通知。</summary>
        void PublishNotification(string appName, string title, string body);

        /// <summary>更新一个监控指标（key 相同视为覆盖）。</summary>
        void UpdateMetric(string key, string label, string value, string unit = "");
    }

    /// <summary>通知源：宿主收到事件后投递到消息面板。</summary>
    public interface INotificationSourcePlugin : IPynelithPlugin
    {
        event EventHandler<PluginNotification>? Received;
    }

    /// <summary>
    /// 监控源：宿主按 <see cref="PollIntervalMs"/> 周期调用 <see cref="Poll"/>。
    /// 实现必须快速返回，禁止在 Poll 里做网络请求；慢操作请在后台线程缓存结果。
    /// </summary>
    public interface IMonitorSourcePlugin : IPynelithPlugin
    {
        /// <summary>建议轮询间隔，最小 1000ms。</summary>
        int PollIntervalMs { get; }

        /// <summary>读取当前指标，可返回空列表。</summary>
        IReadOnlyList<PluginMetric> Poll();
    }

    /// <summary>
    /// AI 后端：提供 OpenAI 兼容的地址与默认模型。宿主选中后写入 Siri 配置。
    /// </summary>
    public interface IAiBackendPlugin : IPynelithPlugin
    {
        /// <summary>显示名称。</summary>
        string DisplayName { get; }

        /// <summary>OpenAI 兼容的 base url，例如 http://localhost:11434/v1。</summary>
        string BaseUrl { get; }

        /// <summary>默认模型名。</summary>
        string DefaultModel { get; }

        /// <summary>列出可用模型；不支持时返回空列表即可。</summary>
        Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);
    }

    /// <summary>动画皮肤：覆盖外观参数。</summary>
    public interface ISkinPlugin : IPynelithPlugin
    {
        /// <summary>显示名称。</summary>
        string DisplayName { get; }

        /// <summary>配色与形态参数。</summary>
        SkinPalette Palette { get; }
    }
}
