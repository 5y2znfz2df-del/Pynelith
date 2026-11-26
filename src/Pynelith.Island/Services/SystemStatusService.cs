using System;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 轻量系统状态采集：电量、屏幕亮度、实时网速。
    ///
    /// 全部在调用线程之外取数（亮度走 WMI，单独后台线程每 10 秒刷新一次），
    /// UI 线程只读缓存字段，不做任何阻塞调用。
    /// </summary>
    public sealed class SystemStatusService : IDisposable
    {
        private long _lastReceived;
        private long _lastSent;
        private DateTime _lastNetworkUtc = DateTime.UtcNow;
        private CancellationTokenSource? _cts;
        private volatile int _brightness = -1;
        private bool _started;

        /// <summary>电量百分比，-1 表示取不到（台式机 / 虚拟机）。</summary>
        public int BatteryPercent { get; private set; } = -1;

        /// <summary>是否接着电源。</summary>
        public bool OnAcPower { get; private set; } = true;

        /// <summary>屏幕亮度百分比，-1 表示该显示器不支持（外接屏常见）。</summary>
        public int BrightnessPercent => _brightness;

        /// <summary>下载速率（KB/s）。</summary>
        public double DownloadKbps { get; private set; }

        /// <summary>上传速率（KB/s）。</summary>
        public double UploadKbps { get; private set; }

        /// <summary>每秒调用一次：电量、网速即时更新，亮度走低频后台刷新。</summary>
        public void Sample()
        {
            if (!_started)
            {
                _started = true;
                _lastReceived = ReadReceivedBytes();
                _lastSent = ReadSentBytes();
                _lastNetworkUtc = DateTime.UtcNow;
                StartBrightnessLoop();
            }

            SamplePower();
            SampleNetwork();
        }

        private void SamplePower()
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status) || status.BatteryLifePercent == 255)
            {
                BatteryPercent = -1;
                return;
            }

            BatteryPercent = status.BatteryLifePercent;
            OnAcPower = status.ACLineStatus == 1;
        }

        private void SampleNetwork()
        {
            long received = ReadReceivedBytes();
            long sent = ReadSentBytes();
            double seconds = Math.Max(0.2, (DateTime.UtcNow - _lastNetworkUtc).TotalSeconds);

            long deltaDown = received - _lastReceived;
            long deltaUp = sent - _lastSent;
            // 网卡重置 / 溢出时会出现负值，直接跳过这一拍
            if (deltaDown >= 0)
            {
                DownloadKbps = deltaDown / 1024.0 / seconds;
            }

            if (deltaUp >= 0)
            {
                UploadKbps = deltaUp / 1024.0 / seconds;
            }

            _lastReceived = received;
            _lastSent = sent;
            _lastNetworkUtc = DateTime.UtcNow;
        }

        private static long ReadReceivedBytes()
            => NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Sum(n => SafeBytes(n, received: true));

        private static long ReadSentBytes()
            => NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Sum(n => SafeBytes(n, received: false));

        private static long SafeBytes(NetworkInterface nic, bool received)
        {
            try
            {
                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                return received ? stats.BytesReceived : stats.BytesSent;
            }
            catch (Exception)
            {
                // 虚拟网卡 / 已拔出的适配器可能直接抛异常
                return 0;
            }
        }

        private void StartBrightnessLoop()
        {
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            // WMI 查询是阻塞调用，放到后台异步循环里，绝不占用 UI 线程。
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    _brightness = ReadBrightness();

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, token);
        }

        private static int ReadBrightness()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                using ManagementObjectCollection results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    object? value = item["CurrentBrightness"];
                    if (value != null)
                    {
                        return Convert.ToInt32(value);
                    }
                }
            }
            catch (Exception)
            {
                // 无 WMI 权限 / 外接显示器不支持亮度控制：保持 -1
            }

            return -1;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        #endregion
    }
}
