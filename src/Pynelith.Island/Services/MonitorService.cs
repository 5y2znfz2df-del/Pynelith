using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace Pynelith.Island.Services
{
    /// <summary>一块显示器的信息。</summary>
    public sealed class MonitorInfo
    {
        /// <summary>设备名（如 \\.\DISPLAY1）。</summary>
        public string DeviceName { get; init; } = string.Empty;

        /// <summary>是否为主显示器。</summary>
        public bool IsPrimary { get; init; }

        /// <summary>工作区（DIP，虚拟屏幕坐标系）。</summary>
        public Rect WorkArea { get; init; }

        /// <summary>整个屏幕区域（DIP）。</summary>
        public Rect Bounds { get; init; }

        /// <summary>该屏的 DPI 缩放（1.0 = 100%）。</summary>
        public double DpiScale { get; init; } = 1.0;

        /// <summary>该屏是否在虚拟屏幕的左上角（用于判断"主屏"）。</summary>
        public bool IsPrimaryByPosition => IsPrimary;
    }

    /// <summary>
    /// 多显示器与逐屏 DPI 支持。
    ///
    /// 问题背景：<c>SystemParameters.WorkArea</c> 只返回"主显示器"的工作区，
    /// 在多屏 / 混合缩放的环境下会把岛钉在错误的屏幕上，或者按错误的 DPI 算坐标。
    ///
    /// 解决方式：用 <c>EnumDisplayMonitors</c> 枚举所有显示器，
    /// 对每一块用 <c>GetDpiForMonitor</c> 取它自己的 DPI，
    /// 工作区则用 <c>GetMonitorInfo</c> 的 <c>rcWork</c>（已是物理像素，需要按 DPI 换算成 DIP）。
    /// </summary>
    public static class MonitorService
    {
        private const int MonitorDefaultToNearest = 2;
        private const int MonitorDpiTypeEffective = 0;

        private const uint MONITORINFOF_PRIMARY = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(
            IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        /// <summary>枚举所有显示器（含每块自己的 DPI 与工作区）。</summary>
        public static IReadOnlyList<MonitorInfo> GetMonitors()
        {
            var result = new List<MonitorInfo>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (!GetMonitorInfoW(hMonitor, ref info))
                    {
                        return true;
                    }

                    double scale = GetScaleForMonitor(hMonitor);

                    var bounds = new Rect(
                        info.rcMonitor.Left / scale,
                        info.rcMonitor.Top / scale,
                        (info.rcMonitor.Right - info.rcMonitor.Left) / scale,
                        (info.rcMonitor.Bottom - info.rcMonitor.Top) / scale);

                    var work = new Rect(
                        info.rcWork.Left / scale,
                        info.rcWork.Top / scale,
                        (info.rcWork.Right - info.rcWork.Left) / scale,
                        (info.rcWork.Bottom - info.rcWork.Top) / scale);

                    result.Add(new MonitorInfo
                    {
                        DeviceName = string.Empty,
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        Bounds = bounds,
                        WorkArea = work,
                        DpiScale = scale,
                    });

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                // 枚举失败：退回到主显示器。
            }

            if (result.Count == 0)
            {
                result.Add(new MonitorInfo
                {
                    IsPrimary = true,
                    WorkArea = SystemParameters.WorkArea,
                    Bounds = new Rect(
                        SystemParameters.VirtualScreenLeft,
                        SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth,
                        SystemParameters.VirtualScreenHeight),
                    DpiScale = 1.0,
                });
            }

            return result;
        }

        /// <summary>
        /// 取包含指定点（虚拟屏幕 DIP 坐标）的显示器。
        /// 找不到时返回主显示器。
        /// </summary>
        public static MonitorInfo GetMonitorAt(Point point)
        {
            var monitors = GetMonitors();

            foreach (var monitor in monitors)
            {
                if (monitor.Bounds.Contains(point))
                {
                    return monitor;
                }
            }

            foreach (var monitor in monitors)
            {
                if (monitor.IsPrimary)
                {
                    return monitor;
                }
            }

            return monitors[0];
        }

        /// <summary>取窗口中心所在显示器的 DPI 缩放（用于逐屏 DPI 判断）。</summary>
        public static double GetScaleForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return 1.0;
            }

            try
            {
                if (!GetWindowRect(hwnd, out var rect))
                {
                    return 1.0;
                }

                var point = new POINT
                {
                    X = (rect.Left + rect.Right) / 2,
                    Y = (rect.Top + rect.Bottom) / 2,
                };

                return GetScaleForPoint(point);
            }
            catch (Exception)
            {
                return 1.0;
            }
        }

        /// <summary>取指定物理屏幕点所在显示器的 DPI 缩放。</summary>
        private static double GetScaleForPoint(POINT physicalPoint)
        {
            try
            {
                IntPtr hMonitor = MonitorFromPoint(physicalPoint, MonitorDefaultToNearest);
                return GetScaleForMonitor(hMonitor);
            }
            catch (Exception)
            {
                return 1.0;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        private static double GetScaleForMonitor(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero)
            {
                return 1.0;
            }

            try
            {
                int hr = GetDpiForMonitor(hMonitor, MonitorDpiTypeEffective, out uint dpiX, out _);
                if (hr == 0 && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch (Exception)
            {
                // shcore.dll 在 Windows 8.1 以下不存在。
            }

            return 1.0;
        }
    }
}
