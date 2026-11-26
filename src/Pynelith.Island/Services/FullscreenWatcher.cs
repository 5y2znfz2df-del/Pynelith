using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 前台全屏窗口监听。
    ///
    /// 设计目标是"零轮询开销"：主路径是 <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>
    /// 事件回调——只有用户切换前台窗口时才判断一次；
    /// 另外保留 2 秒低频兜底，覆盖少数进入全屏却不切换前台窗口的场景（如独占全屏游戏）。
    /// </summary>
    public sealed class FullscreenWatcher : IDisposable
    {
        private const int EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const int WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int DWMWA_CLOAKED = 14;

        private readonly WinEventDelegate _callback;
        private readonly DispatcherTimer _fallback;
        private IntPtr _hook;
        private bool _fullscreen;
        private bool _disposed;

        /// <summary>全屏状态变化（UI 线程触发）。</summary>
        public event EventHandler<bool>? Changed;

        /// <summary>当前是否有全屏应用在前台。</summary>
        public bool IsFullscreen => _fullscreen;

        public FullscreenWatcher()
        {
            _callback = OnWinEvent;
            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _callback,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            // 兜底：全屏切换不一定伴随前台窗口变化，低频复核一次即可
            _fallback = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _fallback.Tick += (_, _) => Evaluate();
        }

        /// <summary>开始监听（会立即判断一次当前状态）。</summary>
        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            _fallback.Start();
            Evaluate();
        }

        public void Stop() => _fallback.Stop();

        private void OnWinEvent(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            => Evaluate();

        /// <summary>重新判断前台是否全屏，只在结果变化时发事件。</summary>
        public void Evaluate()
        {
            if (_disposed)
            {
                return;
            }

            bool fullscreen = IsForegroundFullscreen();
            if (fullscreen == _fullscreen)
            {
                return;
            }

            _fullscreen = fullscreen;
            Changed?.Invoke(this, fullscreen);
        }

        private static bool IsForegroundFullscreen()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
            {
                return false;
            }

            // 自己的窗口（设置页等）不算全屏
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
            {
                return false;
            }

            // 桌面、任务栏、开始菜单等 Shell 窗口不算全屏
            var cls = new StringBuilder(256);
            if (GetClassName(hwnd, cls, cls.Capacity) > 0)
            {
                string name = cls.ToString();
                if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                    or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame")
                {
                    return false;
                }
            }

            // 虚拟桌面里被 DWM 隐形的窗口不算
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }

            if (!GetWindowRect(hwnd, out RECT rect))
            {
                return false;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            // 必须完整覆盖所在显示器的物理区域：普通最大化窗口只覆盖工作区，不会命中
            RECT screen = info.rcMonitor;
            return rect.Left <= screen.Left
                && rect.Top <= screen.Top
                && rect.Right >= screen.Right
                && rect.Bottom >= screen.Bottom;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _fallback.Stop();

            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }

        #region Win32

        private delegate void WinEventDelegate(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        #endregion
    }
}
