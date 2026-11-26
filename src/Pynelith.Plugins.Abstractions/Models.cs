using System;
using System.Collections.Generic;

namespace Pynelith.Plugins
{
    /// <summary>通知源推送的一条消息。</summary>
    public sealed class PluginNotification
    {
        public PluginNotification()
        {
        }

        public PluginNotification(string appName, string title, string body)
        {
            AppName = appName;
            Title = title;
            Body = body;
        }

        /// <summary>来源应用名，显示在消息面板左上角。</summary>
        public string AppName { get; set; } = string.Empty;

        /// <summary>标题。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>正文。</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>UTC 时间戳，宿主用于排序。</summary>
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>监控源产出的一个指标。</summary>
    public sealed class PluginMetric
    {
        public PluginMetric()
        {
        }

        public PluginMetric(string key, string label, string value, string unit = "")
        {
            Key = key;
            Label = label;
            Value = value;
            Unit = unit;
        }

        /// <summary>稳定键名，用于更新而非重复追加。</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>短标签，例如「室外」。</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>已格式化的值，例如「18」。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>单位，例如「°C」。</summary>
        public string Unit { get; set; } = string.Empty;

        /// <summary>拼成一行显示文本。</summary>
        public string ToDisplayText()
        {
            string value = string.IsNullOrEmpty(Unit) ? Value : Value + Unit;
            return string.IsNullOrEmpty(Label) ? value : Label + " " + value;
        }
    }

    /// <summary>动画皮肤提供的配色与形态参数。</summary>
    public sealed class SkinPalette
    {
        /// <summary>强调色（#RRGGBB 或 #AARRGGBB）。</summary>
        public string AccentColor { get; set; } = string.Empty;

        /// <summary>主文字色。</summary>
        public string PrimaryTextColor { get; set; } = string.Empty;

        /// <summary>次级文字色。</summary>
        public string SecondaryTextColor { get; set; } = string.Empty;

        /// <summary>玻璃底色叠加色（#AARRGGBB），空表示不覆盖。</summary>
        public string TintColor { get; set; } = string.Empty;

        /// <summary>圆角，小于 0 表示不覆盖。</summary>
        public double CornerRadius { get; set; } = -1;

        /// <summary>模糊强度 0..1，小于 0 表示不覆盖。</summary>
        public double BlurStrength { get; set; } = -1;
    }

    /// <summary>加载结果，供设置页与日志使用。</summary>
    public sealed class PluginDescriptor
    {
        public PluginManifest Manifest { get; set; } = new PluginManifest();

        /// <summary>插件所在目录。</summary>
        public string Directory { get; set; } = string.Empty;

        public PluginState State { get; set; } = PluginState.Disabled;

        /// <summary>加载耗时（毫秒），用于验证 &lt; 100ms 指标。</summary>
        public double LoadMilliseconds { get; set; }

        /// <summary>失败原因。</summary>
        public string Error { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }
    }
}
