using System;
using System.Runtime.InteropServices;

namespace Pynelith.Glass.Interop
{
    /// <summary>
    /// 操作系统能力探测。使用 RtlGetVersion 而不是 Environment.OSVersion，
    /// 避免应用清单未声明兼容性时返回被伪造的版本号。
    /// </summary>
    internal static class OsInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(out OSVERSIONINFOEX version);

        public static readonly int Build = DetectBuild();

        /// <summary>Windows 11（major >= 11）。</summary>
        public static bool IsWindows11 => DetectMajor() >= 11;

        /// <summary>Win11 22H2+：支持 DWMWA_SYSTEMBACKDROP_TYPE（Mica/Acrylic/Tabbed）。</summary>
        public static bool SupportsSystemBackdrop => IsWindows11 && Build >= 22621;

        /// <summary>Win10 1703+：支持 Windows.UI.Composition 桌面目标与 HostBackdropBrush。</summary>
        public static bool SupportsCompositionBackdrop => Build >= 15063;

        private static int DetectBuild()
        {
            return RawVersion().dwBuildNumber;
        }

        private static int DetectMajor()
        {
            return RawVersion().dwMajorVersion;
        }

        private static OSVERSIONINFOEX RawVersion()
        {
            var info = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>() };
            if (RtlGetVersion(out info) == 0)
            {
                return info;
            }

            var fallback = Environment.OSVersion.Version;
            info.dwMajorVersion = fallback.Major;
            info.dwBuildNumber = fallback.Build;
            return info;
        }
    }
}
