using System;
using System.Runtime.InteropServices;

namespace Pynelith.Glass.Interop
{
    /// <summary>
    /// DWM / user32 / gdi32 原生声明。仅包含渲染模块所需的子集。
    /// </summary>
    internal static class NativeMethods
    {
        #region DWM

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public const int DWMSBT_AUTO = 0;
        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_MAINWINDOW = 2;   // Mica
        public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
        public const int DWMSBT_TABBEDWINDOW = 4;    // Tabbed

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int LeftWidth;
            public int RightWidth;
            public int TopHeight;
            public int BottomHeight;

            /// <summary>整个客户区都视为玻璃边框（DWM 背景透出）。</summary>
            public static MARGINS WholeClientArea => new MARGINS
            {
                LeftWidth = -1,
                RightWidth = -1,
                TopHeight = -1,
                BottomHeight = -1,
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_BLURBEHIND
        {
            public uint dwFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
            public IntPtr hRgn;
            [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;

            public const uint DWM_BB_ENABLE = 0x00000001;
            public const uint DWM_BB_BLURREGION = 0x00000002;
            public const uint DWM_BB_TRANSITIONONMAXIMIZED = 0x00000004;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("dwmapi.dll")]
        public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blur);

        #endregion

        #region User32

        public const int GWL_EXSTYLE = -20;
        public const int GWLP_HWNDPARENT = -8;

        public const long WS_POPUP = 0x80000000L;
        public const long WS_VISIBLE = 0x10000000L;

        public const long WS_EX_NOACTIVATE = 0x08000000L;
        public const long WS_EX_TRANSPARENT = 0x00000020L;
        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_LAYERED = 0x00080000L;
        public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
        public const long WS_EX_TOPMOST = 0x00000008L;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        public const int WM_USER = 0x0400;
        public const int WM_DESTROY = 0x0002;
        public const int WM_WINDOWPOSCHANGED = 0x0047;
        public const int WM_DPICHANGED = 0x02E0;

        public const int GW_HWNDPREV = 3;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            long dwExStyle, string lpClassName, string? lpWindowName,
            long dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hwnd);

        #endregion

        #region Gdi32（区域，用于 Win10 BlurBehind 圆角）

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        #endregion

        #region Kernel32

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        #endregion

        public static void SetSystemBackdrop(IntPtr hwnd, int backdropValue)
        {
            int value = backdropValue;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
        }

        public static void SetCornerPreference(IntPtr hwnd, int preference)
        {
            int value = preference;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }
    }
}
