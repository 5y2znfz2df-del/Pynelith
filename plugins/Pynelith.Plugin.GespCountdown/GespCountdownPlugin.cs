using System;
using System.Collections.Generic;
using System.Globalization;
using Pynelith.Plugins;

namespace Pynelith.Plugin.GespCountdown
{
    /// <summary>
    /// GESP 考试倒计时（监控源）。
    /// 目标日期来自 plugin.json 的 settings，计算本身是纯算术，每次轮询都实时刷新。
    /// </summary>
    public sealed class GespCountdownPlugin : IMonitorSourcePlugin
    {
        private IPluginHost? _host;
        private DateTime _target;
        private bool _parsed;

        public int PollIntervalMs => 60000;

        public void Initialize(IPluginHost host)
        {
            _host = host;

            string rawDate = host.GetSetting("targetDate", string.Empty);
            string rawTime = host.GetSetting("targetTime", "09:00");
            string raw = rawDate + " " + rawTime;

            _parsed = DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _target);

            if (!_parsed)
            {
                host.Log("目标时间无法解析：" + raw);
            }
        }

        public IReadOnlyList<PluginMetric> Poll()
        {
            if (!_parsed)
            {
                return Array.Empty<PluginMetric>();
            }

            string label = _host?.GetSetting("label", "GESP") ?? "GESP";
            TimeSpan left = _target - DateTime.Now;

            string value;
            if (left <= TimeSpan.Zero)
            {
                value = "已结束";
            }
            else if (left.TotalDays >= 1)
            {
                value = string.Format(
                    CultureInfo.InvariantCulture,
                    "还有 {0} 天 {1} 小时",
                    (int)left.TotalDays,
                    left.Hours);
            }
            else
            {
                value = string.Format(
                    CultureInfo.InvariantCulture,
                    "还有 {0} 小时 {1} 分",
                    left.Hours,
                    left.Minutes);
            }

            return new[] { new PluginMetric("gesp", label, value) };
        }

        public void Dispose() => _host = null;
    }
}
