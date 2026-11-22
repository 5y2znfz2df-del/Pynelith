using System;
using System.Runtime.InteropServices;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>快捷键规格：修饰键 + 虚拟键码。</summary>
    public readonly struct HotkeySpec
    {
        public HotkeySpec(uint modifiers, uint virtualKey)
        {
            Modifiers = modifiers;
            VirtualKey = virtualKey;
        }

        /// <summary>修饰键位掩码（MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN）。</summary>
        public uint Modifiers { get; }

        /// <summary>虚拟键码。</summary>
        public uint VirtualKey { get; }
    }

    /// <summary>把 "Ctrl+Alt+S" 这样的字符串解析成 Win32 热键规格。</summary>
    public static class HotkeyParser
    {
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        /// <summary>解析失败时返回 null。</summary>
        public static HotkeySpec? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            uint modifiers = 0;
            uint key = 0;

            foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                string token = part.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= MOD_CONTROL;
                        continue;
                    case "alt":
                        modifiers |= MOD_ALT;
                        continue;
                    case "shift":
                        modifiers |= MOD_SHIFT;
                        continue;
                    case "win":
                    case "windows":
                    case "meta":
                        modifiers |= MOD_WIN;
                        continue;
                }

                if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
                {
                    key = char.ToUpperInvariant(token[0]);
                    continue;
                }

                // F1..F24
                if (token.Length > 1 && token[0] == 'F'
                    && int.TryParse(token[1..], out int fk) && fk >= 1 && fk <= 24)
                {
                    key = (uint)(0x70 + fk - 1);
                    continue;
                }

                return null;
            }

            if (key == 0)
            {
                return null;
            }

            return new HotkeySpec(modifiers, key);
        }
    }

    /// <summary>
    /// Win32 全局热键注册。
    ///
    /// 热键绑定在宿主窗口上，WM_HOTKEY 由宿主自己的消息循环（WPF 的 HwndSource）
    /// 派发并回调，因此这里只负责注册 / 注销，不自己跑消息循环。
    /// </summary>
    internal static class HotkeyNative
    {
        /// <summary>WM_HOTKEY 消息号。</summary>
        public const int WM_HOTKEY = 0x0312;

        /// <summary>本程序使用的热键 ID（同一进程只注册一个）。</summary>
        public const int HotkeyId = 0x5A17;

        private const uint MOD_NOREPEAT = 0x4000;

        /// <summary>最近一次注册失败的 Win32 错误码（0 表示成功）。</summary>
        public static int LastError { get; private set; }

        /// <summary>在指定窗口上注册热键。失败返回 false（通常是被其它程序占用）。</summary>
        public static bool Register(IntPtr hwnd, HotkeySpec spec)
        {
            LastError = 0;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            Unregister(hwnd);
            bool ok = RegisterHotKey(hwnd, HotkeyId, spec.Modifiers | MOD_NOREPEAT, spec.VirtualKey);
            if (!ok)
            {
                LastError = Marshal.GetLastWin32Error();
            }

            return ok;
        }

        /// <summary>注销窗口上的热键（未注册也无害）。</summary>
        public static void Unregister(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            UnregisterHotKey(hwnd, HotkeyId);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
