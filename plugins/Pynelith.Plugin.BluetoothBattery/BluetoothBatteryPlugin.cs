using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Pynelith.Plugins;

namespace Pynelith.Plugin.BluetoothBattery
{
    /// <summary>
    /// 蓝牙设备电量（监控源）。
    ///
    /// 通过配置管理器（cfgmgr32）读取系统为蓝牙设备写入的
    /// <c>DEVPKEY_Bluetooth_Battery</c> 属性：Windows 会定期从 LE 设备的
    /// 电量服务（0x180F）与部分经典蓝牙设备回读并缓存该值，
    /// 插件只读缓存，不主动建立 GATT 连接，因此轮询开销极低。
    ///
    /// 设置项 <c>deviceName</c> 非空时只显示名称包含该关键字的设备。
    /// </summary>
    public sealed class BluetoothBatteryPlugin : IMonitorSourcePlugin
    {
        private IPluginHost? _host;
        private bool _loggedEmpty;

        public int PollIntervalMs => 30000;

        public void Initialize(IPluginHost host)
        {
            _host = host;
            host.Log("蓝牙电量插件已加载：读取系统缓存的 DEVPKEY_Bluetooth_Battery。");
        }

        public IReadOnlyList<PluginMetric> Poll()
        {
            string filter = _host?.GetSetting("deviceName", string.Empty) ?? string.Empty;

            var metrics = new List<PluginMetric>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BluetoothBatteryReader.Entry entry in BluetoothBatteryReader.Read())
            {
                if (!string.IsNullOrWhiteSpace(filter)
                    && entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (!seen.Add(entry.Name))
                {
                    continue;
                }

                metrics.Add(new PluginMetric(
                    "battery:" + entry.Name,
                    entry.Name,
                    entry.Percent.ToString(CultureInfo.InvariantCulture),
                    "%"));

                if (metrics.Count >= 3)
                {
                    break;
                }
            }

            if (metrics.Count == 0)
            {
                if (!_loggedEmpty)
                {
                    _loggedEmpty = true;
                    _host?.Log("暂无可读取的蓝牙电量：设备需已连接，且系统已缓存过电量。");
                }
            }
            else
            {
                _loggedEmpty = false;
            }

            return metrics;
        }

        public void Dispose() => _host = null;

        /// <summary>从配置管理器枚举蓝牙设备节点并读取电量 / 友好名称。</summary>
        private static class BluetoothBatteryReader
        {
            // DEVPKEY_Bluetooth_Battery = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2
            private static readonly Guid BatteryFormatId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
            private const uint BatteryPropertyId = 2;

            // DEVPKEY_Device_FriendlyName = {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 14
            private static readonly Guid DeviceFormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
            private const uint FriendlyNamePropertyId = 14;

            private const uint DevPropTypeByte = 0x00000003;   // DEVPROP_TYPE_BYTE
            private const uint DevPropTypeString = 0x00000012; // DEVPROP_TYPE_STRING

            private const uint GetIdListFilterEnumerator = 0x00000001;
            private const uint LocateDevNodeNormal = 0;

            private static readonly string[] Enumerators = { "BTHLE", "BTHENUM" };
            private static readonly string[] DeviceIdPrefixes = { @"BTHLE\DEV_", @"BTHENUM\DEV_" };

            public readonly struct Entry
            {
                public Entry(string name, byte percent)
                {
                    Name = name;
                    Percent = percent;
                }

                public string Name { get; }

                public byte Percent { get; }
            }

            public static IEnumerable<Entry> Read()
            {
                var results = new List<Entry>();

                foreach (string enumerator in Enumerators)
                {
                    foreach (string deviceId in EnumerateDeviceIds(enumerator))
                    {
                        if (!MatchesPrefix(deviceId))
                        {
                            continue;
                        }

                        if (CM_Locate_DevNodeW(out uint devInst, deviceId, LocateDevNodeNormal) != 0)
                        {
                            continue;
                        }

                        if (!TryReadByte(devInst, BatteryFormatId, BatteryPropertyId, out byte percent))
                        {
                            continue;
                        }

                        string name = ReadString(devInst, DeviceFormatId, FriendlyNamePropertyId);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = ShortName(deviceId);
                        }

                        results.Add(new Entry(name, percent));
                    }
                }

                return results;
            }

            private static IEnumerable<string> EnumerateDeviceIds(string enumerator)
            {
                // 第一次调用取多字符串所需长度（字符数，含结束符）
                if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, GetIdListFilterEnumerator) != 0
                    || length == 0
                    || length > 1_000_000)
                {
                    yield break;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)length * 2);
                try
                {
                    if (CM_Get_Device_ID_ListW(enumerator, buffer, length, GetIdListFilterEnumerator) != 0)
                    {
                        yield break;
                    }

                    string? multi = Marshal.PtrToStringUni(buffer, (int)length);
                    if (string.IsNullOrEmpty(multi))
                    {
                        yield break;
                    }

                    foreach (string id in multi!.Split('\0'))
                    {
                        if (!string.IsNullOrEmpty(id))
                        {
                            yield return id;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            private static bool MatchesPrefix(string deviceId)
            {
                foreach (string prefix in DeviceIdPrefixes)
                {
                    if (deviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool TryReadByte(uint devInst, Guid formatId, uint propertyId, out byte value)
            {
                value = 0;
                var key = new DevPropKey { FormatId = formatId, PropertyId = propertyId };
                IntPtr buffer = Marshal.AllocHGlobal(16);
                try
                {
                    uint size = 16;
                    int result = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0);
                    if (result != 0 || type != DevPropTypeByte || size < 1)
                    {
                        return false;
                    }

                    value = Marshal.ReadByte(buffer);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            private static string ReadString(uint devInst, Guid formatId, uint propertyId)
            {
                var key = new DevPropKey { FormatId = formatId, PropertyId = propertyId };

                // 先探测所需字节数，再按长度分配
                IntPtr probe = Marshal.AllocHGlobal(4);
                try
                {
                    uint size = 0;
                    int result = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, probe, ref size, 0);
                    if ((result != 0 && size == 0) || type != DevPropTypeString || size > 4096)
                    {
                        return string.Empty;
                    }

                    IntPtr buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        uint bufferSize = size;
                        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref bufferSize, 0) != 0)
                        {
                            return string.Empty;
                        }

                        return Marshal.PtrToStringUni(buffer) ?? string.Empty;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(probe);
                }
            }

            /// <summary>友好名称拿不到时，用设备实例 id 尾段当名字。</summary>
            private static string ShortName(string deviceId)
            {
                int index = deviceId.LastIndexOf('\\');
                return index >= 0 && index + 1 < deviceId.Length
                    ? deviceId.Substring(index + 1)
                    : deviceId;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct DevPropKey
            {
                public Guid FormatId;
                public uint PropertyId;
            }

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Get_Device_ID_List_SizeW(
                out uint length,
                string? filter,
                uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Get_Device_ID_ListW(
                string? filter,
                IntPtr buffer,
                uint bufferLength,
                uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Locate_DevNodeW(
                out uint devInst,
                string deviceId,
                uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Get_DevNode_PropertyW(
                uint devInst,
                ref DevPropKey propertyKey,
                out uint propertyType,
                IntPtr propertyBuffer,
                ref uint propertyBufferSize,
                uint flags);
        }
    }
}
