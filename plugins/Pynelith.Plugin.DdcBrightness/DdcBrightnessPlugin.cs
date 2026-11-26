using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Pynelith.Plugins;

namespace Pynelith.Plugin.DdcBrightness
{
    /// <summary>
    /// DDC/CI 显示器亮度（监控源，骨架）。
    ///
    /// 已实现：通过 dxva2.dll 读取主显示器的当前亮度。
    /// 预留：<see cref="SetBrightness"/> 是写入接口，后续接滑块 / 快捷键时直接调用即可。
    /// 注意：笔记本内屏通常不支持 DDC/CI，读取会失败（此时插件静默返回空指标）。
    /// </summary>
    public sealed class DdcBrightnessPlugin : IMonitorSourcePlugin
    {
        private const uint MonitorDefaultToPrimary = 0x00000001;

        private IPluginHost? _host;
        private bool _loggedFailure;

        public int PollIntervalMs => 5000;

        public void Initialize(IPluginHost host) => _host = host;

        public IReadOnlyList<PluginMetric> Poll()
        {
            if (!TryReadBrightness(out uint current, out uint maximum) || maximum == 0)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    _host?.Log("读取亮度失败：显示器可能不支持 DDC/CI，或正被其它程序独占。");
                }

                return Array.Empty<PluginMetric>();
            }

            _loggedFailure = false;
            string label = _host?.GetSetting("label", "亮度") ?? "亮度";
            int percent = (int)Math.Round(current * 100.0 / maximum);

            return new[]
            {
                new PluginMetric(
                    "brightness",
                    label,
                    percent.ToString(CultureInfo.InvariantCulture),
                    "%"),
            };
        }

        /// <summary>预留的写入接口：0 ~ 100。</summary>
        public static bool SetBrightness(int percent)
        {
            IntPtr monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0)
            {
                return false;
            }

            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            {
                return false;
            }

            try
            {
                if (!GetMonitorBrightness(physical[0].hPhysicalMonitor, out uint minimum, out _, out uint maximum) || maximum <= minimum)
                {
                    return false;
                }

                uint clamped = (uint)Math.Clamp(percent, 0, 100);
                uint target = minimum + (maximum - minimum) * clamped / 100u;
                return SetMonitorBrightness(physical[0].hPhysicalMonitor, target);
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }

        private static bool TryReadBrightness(out uint current, out uint maximum)
        {
            current = 0;
            maximum = 0;

            IntPtr monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0)
            {
                return false;
            }

            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            {
                return false;
            }

            try
            {
                for (uint i = 0; i < count; i++)
                {
                    if (GetMonitorBrightness(physical[i].hPhysicalMonitor, out _, out uint value, out uint max) && max > 0)
                    {
                        current = value;
                        maximum = max;
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }

        public void Dispose() => _host = null;

        #region Win32 / dxva2

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor,
            uint physicalMonitorArraySize,
            [Out] PHYSICAL_MONITOR[] physicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint physicalMonitorArraySize, [In] PHYSICAL_MONITOR[] physicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(
            IntPtr hMonitor,
            out uint minimumBrightness,
            out uint currentBrightness,
            out uint maximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

        #endregion
    }
}
