using System;
using System.Collections.Generic;
using Pynelith.Plugins;

namespace Pynelith.Plugin.BluetoothBattery
{
    /// <summary>
    /// 蓝牙设备电量（监控源，骨架）。
    ///
    /// 接入点已经画好：宿主按 <see cref="PollIntervalMs"/> 调 <see cref="Poll"/>，只读缓存。
    /// 后续补全步骤（不影响当前编译与加载）：
    /// 1. 工程 TFM 改为 <c>net8.0-windows10.0.19041.0</c>，即可直接用 WinRT 投影；
    /// 2. 用 <c>BluetoothLEDevice.GetGattServicesAsync</c> 找到 Battery Level 服务（0x180F）；
    /// 3. 读 0x2A19 特征值，写进 <see cref="_cached"/> 并置 <see cref="_hasData"/>。
    /// </summary>
    public sealed class BluetoothBatteryPlugin : IMonitorSourcePlugin
    {
        private IPluginHost? _host;
        private string _cached = "--";
        private bool _hasData;
        private bool _logged;

        public int PollIntervalMs => 60000;

        public void Initialize(IPluginHost host)
        {
            _host = host;

            if (!_logged)
            {
                _logged = true;
                host.Log("蓝牙电量插件已加载（骨架版）：设备枚举尚未接入，暂不显示指标。");
            }
        }

        public IReadOnlyList<PluginMetric> Poll()
        {
            if (!_hasData)
            {
                return Array.Empty<PluginMetric>();
            }

            string label = _host?.GetSetting("label", "蓝牙") ?? "蓝牙";
            return new[] { new PluginMetric("bluetooth", label, _cached, "%") };
        }

        /// <summary>设备枚举完成后由后台线程写入（预留给 WinRT 实现）。</summary>
        private void Publish(int percent)
        {
            _cached = percent.ToString();
            _hasData = true;
        }

        public void Dispose() => _host = null;
    }
}
