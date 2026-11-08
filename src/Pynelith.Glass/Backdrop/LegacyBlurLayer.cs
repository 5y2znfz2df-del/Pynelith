using System;
using System.Windows;
using System.Windows.Media;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// Win10 兼容层：DwmEnableBlurBehindWindow + 圆角区域。
    /// 这是 Aero 时代的玻璃通道，Win10 上仍由 DWM 硬件合成，性能可控。
    /// </summary>
    internal sealed class LegacyBlurLayer : IGlassLayer
    {
        private IntPtr _hwnd;
        private bool _enabled;

        public GlassBackdropKind Kind => GlassBackdropKind.LegacyBlur;

        public bool TryInitialize(IntPtr ownerHwnd, GlassOptions options)
        {
            _hwnd = ownerHwnd;
            Apply(IntPtr.Zero, 0f);
            _enabled = true;
            return true;
        }

        public void OnOwnerRectChanged(Rect ownerRectDevice, float cornerRadiusDevice)
        {
            if (!_enabled)
            {
                return;
            }

            int w = Math.Max(1, (int)Math.Round(ownerRectDevice.Width));
            int h = Math.Max(1, (int)Math.Round(ownerRectDevice.Height));
            int d = Math.Max(0, (int)Math.Round(cornerRadiusDevice * 2));

            IntPtr region = d > 0
                ? NativeMethods.CreateRoundRectRgn(0, 0, w, h, d, d)
                : NativeMethods.CreateRectRgn(0, 0, w, h);

            Apply(region, cornerRadiusDevice);
        }

        private void Apply(IntPtr region, float radius)
        {
            var blur = new NativeMethods.DWM_BLURBEHIND
            {
                dwFlags = NativeMethods.DWM_BLURBEHIND.DWM_BB_ENABLE | NativeMethods.DWM_BLURBEHIND.DWM_BB_BLURREGION,
                fEnable = true,
                hRgn = region,
            };

            NativeMethods.DwmEnableBlurBehindWindow(_hwnd, ref blur);

            if (radius > 0)
            {
                NativeMethods.SetCornerPreference(_hwnd, 2 /*Round*/);
            }
        }

        public void OnOwnerVisibilityChanged(bool visible)
        {
        }

        public void SetBlurStrength(double strength)
        {
            // 旧接口不支持调节半径。
        }

        public void SetTint(Color color, double opacity)
        {
            // 由 UI 在半透明背景上叠加着色层。
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            {
                var blur = new NativeMethods.DWM_BLURBEHIND
                {
                    dwFlags = NativeMethods.DWM_BLURBEHIND.DWM_BB_ENABLE,
                    fEnable = false,
                    hRgn = IntPtr.Zero,
                };
                NativeMethods.DwmEnableBlurBehindWindow(_hwnd, ref blur);
            }

            _enabled = false;
            _hwnd = IntPtr.Zero;
        }
    }
}
