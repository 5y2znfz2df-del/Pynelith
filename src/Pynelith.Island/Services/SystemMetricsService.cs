using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// CPU / 内存实时占用采样。
    /// 只用 Win32（GetSystemTimes + GlobalMemoryStatusEx），不依赖 PerformanceCounter，
    /// 因此不需要额外 NuGet 包，也不会因为性能计数器损坏而导致启动失败。
    /// 采样在 UI 线程的 DispatcherTimer 上执行，单次开销只有两次系统调用。
    /// </summary>
    public sealed class SystemMetricsService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private ulong _lastIdle;
        private ulong _lastKernel;
        private ulong _lastUser;
        private bool _primed;
        private bool _disposed;

        /// <summary>采样完成（UI 线程）。</summary>
        public event EventHandler? Updated;

        /// <summary>CPU 总占用百分比 0..100。</summary>
        public double CpuPercent { get; private set; }

        /// <summary>物理内存占用百分比 0..100。</summary>
        public double MemoryPercent { get; private set; }

        /// <summary>已用物理内存（GB）。</summary>
        public double UsedMemoryGb { get; private set; }

        /// <summary>物理内存总量（GB）。</summary>
        public double TotalMemoryGb { get; private set; }

        /// <summary>系统是否提供了有效数据。</summary>
        public bool IsAvailable { get; private set; }

        public SystemMetricsService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _timer.Tick += (_, _) => SafeSample();
        }

        /// <summary>立即开始采样（第一次只建立基线，1 秒后出第一个值）。</summary>
        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            SafeSample();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _primed = false;
        }

        private void SafeSample()
        {
            try
            {
                Sample();
                Updated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 单次采样失败（系统接口偶发失败）不应影响岛的正常运行。
            }
        }

        private void Sample()
        {
            var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                if (memory.ullTotalPhys > 0)
                {
                    // 用字节数算百分比（而不是整数 dwMemoryLoad），保留小数位，
                    // 数值会随真实占用小幅变动，也更接近任务管理器的「使用中」。
                    const double toGb = 1.0 / (1024 * 1024 * 1024);
                    ulong used = memory.ullTotalPhys > memory.ullAvailPhys
                        ? memory.ullTotalPhys - memory.ullAvailPhys
                        : 0;
                    TotalMemoryGb = memory.ullTotalPhys * toGb;
                    UsedMemoryGb = used * toGb;
                    MemoryPercent = Math.Clamp(used * 100.0 / memory.ullTotalPhys, 0, 100);
                }
                else
                {
                    MemoryPercent = Math.Clamp(memory.dwMemoryLoad, 0, 100);
                }

                IsAvailable = true;
            }

            if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                return;
            }

            ulong idleTicks = ToTicks(idle);
            ulong kernelTicks = ToTicks(kernel);
            ulong userTicks = ToTicks(user);

            if (_primed)
            {
                ulong idleDelta = idleTicks - _lastIdle;
                ulong kernelDelta = kernelTicks - _lastKernel;
                ulong userDelta = userTicks - _lastUser;

                // kernel 时间已经包含 idle，因此总量 = kernel + user，忙碌 = 总量 - idle
                ulong total = kernelDelta + userDelta;
                if (total > 0)
                {
                    ulong busy = total > idleDelta ? total - idleDelta : 0;
                    CpuPercent = Math.Clamp(busy * 100.0 / total, 0, 100);
                    IsAvailable = true;
                }
            }

            _lastIdle = idleTicks;
            _lastKernel = kernelTicks;
            _lastUser = userTicks;
            _primed = true;
        }

        private static ulong ToTicks(FILETIME time)
            => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        #endregion
    }
}
