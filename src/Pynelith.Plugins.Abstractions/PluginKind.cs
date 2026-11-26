using System;

namespace Pynelith.Plugins
{
    /// <summary>
    /// 插件类型。严格限定四类，拒绝重型扩展点。
    /// </summary>
    public enum PluginKind
    {
        /// <summary>通知源：向灵动岛推送消息。</summary>
        NotificationSource = 0,

        /// <summary>监控源：周期性提供一段可显示的指标文本。</summary>
        MonitorSource = 1,

        /// <summary>AI 后端：提供 OpenAI 兼容的地址与模型列表。</summary>
        AiBackend = 2,

        /// <summary>动画皮肤：覆盖强调色、圆角、模糊强度等外观参数。</summary>
        Skin = 3,
    }

    /// <summary>插件加载状态，用于设置页展示。</summary>
    public enum PluginState
    {
        /// <summary>已落盘但未加载（被禁用或尚未扫描）。</summary>
        Disabled = 0,

        /// <summary>加载成功且已调用 Initialize。</summary>
        Loaded = 1,

        /// <summary>加载失败，原因见 Error。</summary>
        Failed = 2,

        /// <summary>清单本身不合法（缺字段、类型对不上等）。</summary>
        Invalid = 3,
    }

    /// <summary>清单解析 / 加载失败的异常。携带人类可读原因。</summary>
    public sealed class PluginException : Exception
    {
        public PluginException(string message)
            : base(message)
        {
        }

        public PluginException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
