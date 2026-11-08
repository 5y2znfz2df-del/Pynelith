using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// 背景模糊合成层 —— 移植 WinIsland “glass” 风格的窗口架构：
    /// 一个 WS_EX_NOREDIRECTIONBITMAP 伴随窗口贴在 owner 之下，
    /// 由 DWM 在该窗口上合成“桌面背景模糊”（Win11 用 SYSTEMBACKDROP Acrylic，
    /// Win10 用 BlurBehind），并用窗口区域（Rgn）裁剪成圆角/自定义形状。
    ///
    /// 说明：WinIsland 原版用 Windows.UI.Composition HostBackdropBrush + D2D 高斯模糊；
    /// 该效果类已不在现代公开桌面元数据中（CompositionEffectFactory 的官方效果来源是
    /// Win2D/UWP 专属包）。DWM 系统背景正是微软为桌面应用提供的同能力公开通道
    /// （WinUI 3 的 DesktopAcrylic 底层即此），全程 GPU 合成、本进程零着色器开销。
    /// </summary>
    internal sealed class HostBackdropLayer : IGlassLayer
    {
        private const string BackdropClassName = "PynelithGlassBackdropHost";

        private static readonly WndProc _classWndProc = WndProcImpl;
        private static bool _classRegistered;
        private static readonly object _classLock = new object();

        private IntPtr _ownerHwnd;
        private IntPtr _companionHwnd;
        private bool _ready;
        private bool _useSystemBackdrop;

        public GlassBackdropKind Kind => GlassBackdropKind.CompositionBlur;

        public bool TryInitialize(IntPtr ownerHwnd, GlassOptions options)
        {
            if (!NativeMethods.GetWindowRect(ownerHwnd, out var rect))
            {
                return false;
            }

            _ownerHwnd = ownerHwnd;
            _useSystemBackdrop = OsInfo.SupportsSystemBackdrop;

            try
            {
                RegisterClassIfNeeded();

                _companionHwnd = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT
                    | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOREDIRECTIONBITMAP,
                    BackdropClassName, null,
                    NativeMethods.WS_POPUP,
                    rect.Left, rect.Top,
                    Math.Max(1, rect.Right - rect.Left),
                    Math.Max(1, rect.Bottom - rect.Top),
                    IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.GetModuleHandle(null), IntPtr.Zero);

                if (_companionHwnd == IntPtr.Zero)
                {
                    return false;
                }

                var margins = NativeMethods.MARGINS.WholeClientArea;
                NativeMethods.DwmExtendFrameIntoClientArea(_companionHwnd, ref margins);

                if (_useSystemBackdrop)
                {
                    // Win11：DWM 桌面模糊（与 WinUI DesktopAcrylic 同通道）
                    NativeMethods.SetSystemBackdrop(_companionHwnd, NativeMethods.DWMSBT_TRANSIENTWINDOW);
                }
                else
                {
                    // Win10：Aero 玻璃模糊通道
                    var blur = new NativeMethods.DWM_BLURBEHIND
                    {
                        dwFlags = NativeMethods.DWM_BLURBEHIND.DWM_BB_ENABLE | NativeMethods.DWM_BLURBEHIND.DWM_BB_BLURREGION,
                        fEnable = true,
                        hRgn = IntPtr.Zero,
                    };
                    NativeMethods.DwmEnableBlurBehindWindow(_companionHwnd, ref blur);
                }

                NativeMethods.ShowWindow(_companionHwnd, NativeMethods.SW_SHOW);
                OnOwnerRectChanged(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                                   options.CornerRadius * 96f / Math.Max(96u, NativeMethods.GetDpiForWindow(ownerHwnd)));
                _ready = true;
                return true;
            }
            catch (Exception)
            {
                Dispose();
                return false;
            }
        }

        public void OnOwnerRectChanged(Rect ownerRectDevice, float cornerRadiusDevice)
        {
            if (!_ready)
            {
                return;
            }

            int w = Math.Max(1, (int)ownerRectDevice.Width);
            int h = Math.Max(1, (int)ownerRectDevice.Height);

            NativeMethods.SetWindowPos(
                _companionHwnd, IntPtr.Zero,
                (int)ownerRectDevice.X, (int)ownerRectDevice.Y, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);

            ApplyShape(w, h, cornerRadiusDevice);
            SyncZOrder();
        }

        private void ApplyShape(int width, int height, float radiusDevice)
        {
            if (radiusDevice <= 0f)
            {
                SetWindowRgn(_companionHwnd, IntPtr.Zero, true);
                return;
            }

            int d = Math.Max(1, (int)Math.Round(radiusDevice * 2));
            IntPtr rgn = NativeMethods.CreateRoundRectRgn(0, 0, width, height, d, d);
            if (rgn != IntPtr.Zero)
            {
                SetWindowRgn(_companionHwnd, rgn, true); // 成功时区域归系统所有，不可再 DeleteObject
            }
        }

        public void OnOwnerVisibilityChanged(bool visible)
        {
            if (_ready)
            {
                NativeMethods.ShowWindow(_companionHwnd, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
                if (visible)
                {
                    SyncZOrder();
                }
            }
        }

        /// <summary>把伴随窗口插到 owner 正下方的 Z 序位置。</summary>
        private void SyncZOrder()
        {
            IntPtr insertAfter = NativeMethods.GetWindow(_ownerHwnd, NativeMethods.GW_HWNDPREV);
            if (insertAfter == IntPtr.Zero)
            {
                insertAfter = NativeMethods.HWND_BOTTOM;
            }

            NativeMethods.SetWindowPos(_companionHwnd, insertAfter, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        public void SetBlurStrength(double strength)
        {
            // DWM 系统背景模糊半径由主题固定；保留接口以兼容统一调用。
        }

        public void SetTint(System.Windows.Media.Color color, double opacity)
        {
            // 着色层由 UI 侧半透明内容叠加（伴随窗口只负责背景模糊）。
        }

        private static void RegisterClassIfNeeded()
        {
            lock (_classLock)
            {
                if (_classRegistered)
                {
                    return;
                }

                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_classWndProc),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = BackdropClassName,
                    lpszMenuName = null,
                    hIconSm = IntPtr.Zero,
                };

                ushort atom = RegisterClassEx(ref wc);
                if (atom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    // 1812/1813: 类已注册（重复加载），视为成功
                    if (err != 1812 && err != 1813)
                    {
                        throw new InvalidOperationException($"RegisterClassEx failed: {err}");
                    }
                }

                _classRegistered = true;
            }
        }

        private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        public void Dispose()
        {
            if (_companionHwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_companionHwnd);
                _companionHwnd = IntPtr.Zero;
            }

            _ready = false;
        }
    }
}
