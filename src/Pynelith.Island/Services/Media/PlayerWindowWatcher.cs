using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Pynelith.Island.Services.Media
{
    /// <summary>
    /// 播放器窗口标题监听器。
    ///
    /// 用途：网易云音乐的 SMTC 时间轴恒为 0，只能靠"换歌事件"推算 0 点，
    /// 而换歌事件（网络请求 + 元数据回传）比音频真正开始要晚几百毫秒，
    /// 于是歌词整体偏晚。主窗口标题里一直挂着当前曲目（"歌名 - 歌手"），
    /// 标题一变就说明换歌已经发生 —— 这是比 SMTC 元数据事件更准的 0 点信号。
    ///
    /// 时间点估计：标题变化一定发生在「上一次读到旧标题」与「这一次读到新标题」之间。
    /// 这里取上一次轮询时刻作为变化时刻（偏早的一端），
    /// 因为歌词早出场几��百毫秒远好过晚出场 —— 晚出场会直接错过第一句。
    ///
    /// 实现：EnumWindows 找到目标进程的主窗口，读窗口标题。
    /// 只在需要辅助同步时轮询，开销可以忽略。
    /// </summary>
    internal sealed class PlayerWindowWatcher : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private readonly object _gate = new object();
        private IntPtr _hwnd;
        private uint _processId;
        private string _lastTitle = string.Empty;
        private DateTime _lastPollUtc = DateTime.MinValue;
        private bool _primed;
        private bool _disposed;

        /// <summary>
        /// 标题发生变化时触发。
        /// 参数一为新标题，参数二为「标题实际变化时刻」的估计值（取上一次轮询时刻）。
        /// </summary>
        public event Action<string, DateTime>? TitleChanged;

        /// <summary>绑定到目标进程的主窗口。进程退出后调用方需要重新绑定。</summary>
        public void Attach(uint processId)
        {
            lock (_gate)
            {
                _processId = processId;
                _hwnd = IntPtr.Zero;
                _lastTitle = string.Empty;
                _lastPollUtc = DateTime.MinValue;

                // 首次读到标题只用来"定基线"，不触发换歌事件：
                // 否则刚绑定就会把播放位置重置为 0。
                _primed = false;
            }
        }

        /// <summary>解绑并停止跟踪。</summary>
        public void Detach()
        {
            lock (_gate)
            {
                _hwnd = IntPtr.Zero;
                _processId = 0;
                _lastTitle = string.Empty;
                _lastPollUtc = DateTime.MinValue;
                _primed = false;
            }
        }

        /// <summary>
        /// 轮询一次：找到目标进程的主窗口并读取标题。
        /// 标题变化时触发 <see cref="TitleChanged"/>。
        /// </summary>
        public void Poll()
        {
            IntPtr hwnd;
            uint processId;

            lock (_gate)
            {
                if (_disposed || _processId == 0)
                {
                    return;
                }

                hwnd = _hwnd;
                processId = _processId;
            }

            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                hwnd = FindMainWindow(processId);
                lock (_gate)
                {
                    _hwnd = hwnd;
                }

                if (hwnd == IntPtr.Zero)
                {
                    return;
                }
            }

            string title = ReadTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            DateTime assumedChangeUtc;
            string previousTitle;

            lock (_gate)
            {
                previousTitle = _lastTitle;

                if (!_primed)
                {
                    // 首次：只记录基线，不当作换歌。
                    _primed = true;
                    _lastTitle = title;
                    _lastPollUtc = DateTime.UtcNow;
                    return;
                }

                if (string.Equals(title, _lastTitle, StringComparison.Ordinal))
                {
                    _lastPollUtc = DateTime.UtcNow;
                    return;
                }

                // 标题变化发生在上一次轮询之后：用上一次轮询时刻作为变化时刻。
                assumedChangeUtc = _lastPollUtc != DateTime.MinValue
                    ? _lastPollUtc
                    : DateTime.UtcNow;

                _lastTitle = title;
                _lastPollUtc = DateTime.UtcNow;
            }

            _ = previousTitle;
            TitleChanged?.Invoke(title, assumedChangeUtc);
        }

        /// <summary>从窗口标题里拆出歌名与歌手（"歌名 - 歌手" → 歌名/歌手）。</summary>
        public static (string Title, string Artist) SplitTitle(string windowTitle, string separator)
        {
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                return (string.Empty, string.Empty);
            }

            int index = windowTitle.LastIndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= windowTitle.Length)
            {
                return (windowTitle.Trim(), string.Empty);
            }

            string title = windowTitle[..index].Trim();
            string artist = windowTitle[(index + separator.Length)..].Trim();
            return (title, artist);
        }

        private static IntPtr FindMainWindow(uint processId)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != processId)
                {
                    return true;
                }

                // 只看可见窗口；无标题的窗口（工具窗、托盘窗）直接跳过。
                if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) <= 0)
                {
                    return true;
                }

                if (!GetWindowRect(hwnd, out var rect))
                {
                    return true;
                }

                // 面积最大的可见窗口当作主窗口。
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }

                return true;
            }, IntPtr.Zero);

            return best;
        }

        private static string ReadTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(length + 1);
            int copied = GetWindowText(hwnd, sb, sb.Capacity);
            return copied > 0 ? sb.ToString() : string.Empty;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _hwnd = IntPtr.Zero;
                _processId = 0;
            }
        }
    }
}
