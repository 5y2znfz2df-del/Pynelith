using System;
using System.Runtime.InteropServices;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 系统主音量（WASAPI IAudioEndpointVolume）。
    /// SMTC 本身不提供音量接口，因此岛内的音量滑杆直接控制系统输出音量，
    /// 与 Windows 音量键 / 任务栏音量保持一致。
    /// </summary>
    public static class SystemVolumeService
    {
        private static IAudioEndpointVolume? _endpoint;
        private static DateTime _nextRetryUtc = DateTime.MinValue;

        /// <summary>当前系统是否提供了可用的音频输出端点。</summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>读取主音量 0..1。</summary>
        public static double GetVolume()
        {
            IAudioEndpointVolume? volume = EnsureEndpoint();
            if (volume == null)
            {
                return 0;
            }

            try
            {
                volume.GetMasterVolumeLevelScalar(out float level);
                return Math.Clamp(level, 0, 1);
            }
            catch (Exception)
            {
                Invalidate();
                return 0;
            }
        }

        /// <summary>设置主音量 0..1。</summary>
        public static void SetVolume(double level)
        {
            IAudioEndpointVolume? volume = EnsureEndpoint();
            if (volume == null)
            {
                return;
            }

            try
            {
                Guid context = Guid.Empty;
                volume.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref context);
            }
            catch (Exception)
            {
                // 设备切换瞬间可能失败：丢弃端点，下一次调用重新枚举默认输出设备。
                Invalidate();
            }
        }

        /// <summary>端点失效（耳机拔出 / 默认设备切换）后允许重新初始化。</summary>
        private static void Invalidate()
        {
            _endpoint = null;
            IsAvailable = false;
            _nextRetryUtc = DateTime.UtcNow.AddMilliseconds(800);
        }

        private static IAudioEndpointVolume? EnsureEndpoint()
        {
            if (_endpoint != null)
            {
                return _endpoint;
            }

            // 没有音频设备时不要每次拖动都做一次 COM 枚举，退避后再试
            if (DateTime.UtcNow < _nextRetryUtc)
            {
                return null;
            }

            try
            {
                Guid clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                Type? enumeratorType = Type.GetTypeFromCLSID(clsid);
                if (enumeratorType == null)
                {
                    _nextRetryUtc = DateTime.UtcNow.AddSeconds(3);
                    return null;
                }

                var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;

                // eRender = 0, eConsole = 0
                int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice device);
                if (hr != 0 || device == null)
                {
                    _nextRetryUtc = DateTime.UtcNow.AddSeconds(3);
                    return null;
                }

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object instance);
                if (hr != 0 || instance is not IAudioEndpointVolume volume)
                {
                    _nextRetryUtc = DateTime.UtcNow.AddSeconds(3);
                    return null;
                }

                _endpoint = volume;
                IsAvailable = true;
                return _endpoint;
            }
            catch (Exception)
            {
                _nextRetryUtc = DateTime.UtcNow.AddSeconds(3);
                return null;
            }
        }

        #region COM 定义

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);

            [PreserveSig]
            int OpenPropertyStore(uint access, out IntPtr properties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

            [PreserveSig]
            int GetState(out uint state);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig]
            int RegisterControlChangeNotify(IntPtr notify);

            [PreserveSig]
            int UnregisterControlChangeNotify(IntPtr notify);

            [PreserveSig]
            int GetChannelCount(out uint count);

            [PreserveSig]
            int SetMasterVolumeLevel(float level, ref Guid eventContext);

            [PreserveSig]
            int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

            [PreserveSig]
            int GetMasterVolumeLevel(out float level);

            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float level);
        }

        #endregion
    }
}
