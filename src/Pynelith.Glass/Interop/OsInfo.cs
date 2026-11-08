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
        /// <summary>RTL_OSVERSIONINFOEXW 的完整大小（含 128 字符 CSD 串）。</summary>
        private const int VersionInfoSize = 284;

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(IntPtr versionInfo);

        private static readonly int _major;
        private static readonly int _build;

        static OsInfo()
        {
            if (!TryReadNative(out _major, out _build))
            {
                var fallback = Environment.OSVersion.Version;
                _major = fallback.Major;
                _build = fallback.Build;
            }
        }

        /// <summary>内核版本号（Win11 为 22000+）。</summary>
        public static int Build => _build;

        /// <summary>
        /// Windows 11 判定。注意 Win11 的 MajorVersion 仍是 10，
        /// 必须用 Build ≥ 22000 区分（Win10 10.0.1904x）。
        /// </summary>
        public static bool IsWindows11 => _major > 10 || (_major == 10 && _build >= 22000);

        /// <summary>Win11 22H2+：支持 DWMWA_SYSTEMBACKDROP_TYPE（Mica/Acrylic/Tabbed）。</summary>
        public static bool SupportsSystemBackdrop => IsWindows11 && Build >= 22621;

        /// <summary>Win10 1703+：支持伴随窗口（WS_EX_NOREDIRECTIONBITMAP）背景模糊层。</summary>
        public static bool SupportsCompositionBackdrop => Build >= 15063;

        private static bool TryReadNative(out int major, out int build)
        {
            major = build = 0;

            IntPtr buffer = Marshal.AllocHGlobal(VersionInfoSize);
            try
            {
                // 必须先写入结构大小，否则 RtlGetVersion 直接失败。
                for (int offset = 0; offset < VersionInfoSize; offset += 4)
                {
                    Marshal.WriteInt32(buffer, offset, 0);
                }

                Marshal.WriteInt32(buffer, 0, VersionInfoSize);

                if (RtlGetVersion(buffer) != 0)
                {
                    return false;
                }

                major = Marshal.ReadInt32(buffer, 4);
                build = Marshal.ReadInt32(buffer, 12);
                return major > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
