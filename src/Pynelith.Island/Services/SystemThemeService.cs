using System;
using Microsoft.Win32;

namespace Pynelith.Island.Services
{
    /// <summary>系统深浅色模式。</summary>
    public enum SystemThemeMode
    {
        /// <summary>浅色。</summary>
        Light,

        /// <summary>深色。</summary>
        Dark,
    }

    /// <summary>
    /// 系统深浅色模式监听。
    ///
    /// Windows 把用户的深浅色设置写在
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>
    /// 的 <c>AppsUseLightTheme</c>（0 = 深色，1 = 浅色）里。
    ///
    /// 这个值没有官方的事件通知，只能靠轮询 + 注册表变更通知来感知。
    /// 这里用两个来源：
    /// 1. 定时轮询（2 秒一次，代价极低）；
    /// 2. <c>SystemEvents.UserPreferenceChanged</c>（主题类别变化时立刻回调）。
    ///
    /// 深浅色对「纯黑 / 磨砂」两套主题影响最大：
    /// 深色下纯黑与桌面融为一体，浅色下则需要把底色调亮、把描边压暗。
    /// </summary>
    public sealed class SystemThemeService : IDisposable
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private const string LightThemeValue = "AppsUseLightTheme";

        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private bool _disposed;

        public SystemThemeService()
        {
            Current = ReadSystemTheme();

            _timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _timer.Tick += (_, _) => CheckForChange();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _timer.Start();
        }

        /// <summary>当前系统主题。</summary>
        public SystemThemeMode Current { get; private set; }

        /// <summary>系统主题变化时触发（UI 线程）。</summary>
        public event EventHandler? ThemeChanged;

        /// <summary>读取当前系统深浅色模式；读不到时按深色处理。</summary>
        public static SystemThemeMode ReadSystemTheme()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue(LightThemeValue) is int value)
                {
                    return value != 0 ? SystemThemeMode.Light : SystemThemeMode.Dark;
                }
            }
            catch (Exception)
            {
                // 注册表不可读时按深色处理（与纯黑主题最搭）。
            }

            return SystemThemeMode.Dark;
        }

        private void CheckForChange()
        {
            var latest = ReadSystemTheme();
            if (latest == Current)
            {
                return;
            }

            Current = latest;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 只关心"整体/主题"类别的变化；其余情况交给轮询兜底。
            if (e.Category != UserPreferenceCategory.General
                && e.Category != UserPreferenceCategory.VisualStyle)
            {
                return;
            }

            CheckForChange();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }
}
