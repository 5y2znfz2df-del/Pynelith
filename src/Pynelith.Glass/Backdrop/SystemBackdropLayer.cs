using System;
using System.Windows;
using System.Windows.Media;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// Win11 22H2+ 系统背景层：DWMWA_SYSTEMBACKDROP_TYPE。
    /// 模糊与桌面采样完全由 DWM 在合成器侧完成，本进程零 GPU 开销，
    /// 与 WinIsland 的 glass 思路同源（系统级 backdrop），但走的是更省资源的 DWM 通道。
    /// </summary>
    internal sealed class SystemBackdropLayer : IGlassLayer
    {
        private IntPtr _hwnd;

        public GlassBackdropKind Kind => GlassBackdropKind.SystemBackdrop;

        public bool TryInitialize(IntPtr ownerHwnd, GlassOptions options)
        {
            if (!OsInfo.SupportsSystemBackdrop)
            {
                return false;
            }

            _hwnd = ownerHwnd;

            // 1. 扩展玻璃边框到整个客户区，使 DWM 背景能透出 WPF 内容。
            var margins = NativeMethods.MARGINS.WholeClientArea;
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            // 2. 设置系统背景类型。
            int backdrop = options.SystemStyle switch
            {
                GlassSystemStyle.Mica => NativeMethods.DWMSBT_MAINWINDOW,
                GlassSystemStyle.Tabbed => NativeMethods.DWMSBT_TABBEDWINDOW,
                _ => NativeMethods.DWMSBT_TRANSIENTWINDOW,
            };
            NativeMethods.SetSystemBackdrop(_hwnd, backdrop);

            // 3. 圆角：DWM 接管（半径由系统主题决定，约 8px）。
            NativeMethods.SetCornerPreference(_hwnd, options.CornerRadius > 0f ? 2 /*Round*/ : 1 /*DontRound*/);
            return true;
        }

        public void OnOwnerRectChanged(Rect ownerRectDevice, float cornerRadiusDevice)
        {
            // DWM 全窗背景，无需逐帧同步。
        }

        public void OnOwnerVisibilityChanged(bool visible)
        {
        }

        public void SetBlurStrength(double strength)
        {
            // 系统背景模糊强度由 DWM/主题控制，不开放调节。
        }

        public void SetTint(Color color, double opacity)
        {
            // 着色层由 UI 在半透明背景上自行叠加（SystemBackdrop 模式的标准做法）。
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            {
                var margins = new NativeMethods.MARGINS();
                NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
                NativeMethods.SetSystemBackdrop(_hwnd, NativeMethods.DWMSBT_NONE);
            }

            _hwnd = IntPtr.Zero;
        }
    }
}
