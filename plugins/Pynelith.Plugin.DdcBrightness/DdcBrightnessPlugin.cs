using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pynelith.Plugins;

namespace Pynelith.Plugin.DdcBrightness
{
    /// <summary>
    /// 显示器亮度（监控源）。
    ///
    /// 优先通过 dxva2.dll 走 DDC/CI 读取外接显示器亮度；
    /// 笔记本内屏不支持 DDC/CI，此时退回 WMI（WmiMonitorBrightness）读取内屏亮度。
    /// 预留：<see cref="SetBrightness"/> 是 DDC/CI 写入接口，后续接滑块 / 快捷键时直接调用即可。
    /// </summary>
    public sealed class DdcBrightnessPlugin : IMonitorSourcePlugin
    {
        private const uint MonitorDefaultToPrimary = 0x00000001;

        private IPluginHost? _host;
        private bool _loggedFailure;

        public int PollIntervalMs => 5000;

        public void Initialize(IPluginHost host) => _host = host;

        [SupportedOSPlatform("windows")]
        public IReadOnlyList<PluginMetric> Poll()
        {
            int percent;

            if (TryReadBrightness(out uint current, out uint maximum) && maximum > 0)
            {
                percent = (int)Math.Round(current * 100.0 / maximum);
            }
            else if (TryReadWmiBrightness(out int wmiPercent))
            {
                // 笔记本内屏：DDC/CI 不可用，WMI 仍能给出百分比
                percent = wmiPercent;
            }
            else
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

            return new[]
            {
                new PluginMetric(
                    "brightness",
                    label,
                    Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture),
                    "%"),
            };
        }

        /// <summary>
        /// 笔记本内屏亮度：WMI 的 WmiMonitorBrightness.CurrentBrightness。
        /// 走 COM 晚绑定（WbemScripting.SWbemLocator），不引入额外的程序集依赖。
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static bool TryReadWmiBrightness(out int percent)
        {
            percent = 0;

            try
            {
                Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (locatorType == null)
                {
                    return false;
                }

                object? locator = Activator.CreateInstance(locatorType);
                if (locator == null)
                {
                    return false;
                }

                dynamic wmi = locator;
                dynamic service = wmi.ConnectServer(".", @"root\WMI");
                dynamic results = service.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness");

                foreach (dynamic item in results)
                {
                    object value = item.CurrentBrightness;
                    if (value == null)
                    {
                        continue;
                    }

                    percent = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return percent >= 0;
                }
            }
            catch (Exception)
            {
                // 无 WMI 服务 / 权限时静默失败，交给上层返回空指标
            }

            return false;
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
