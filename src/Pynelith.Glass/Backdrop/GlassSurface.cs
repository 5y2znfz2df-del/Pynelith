using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// 玻璃表面实现：负责窗口消息钩子、DIP→设备像素换算、
    /// 层级选择与自动降级；对 UI 模块只暴露 IGlassSurface。
    /// </summary>
    internal sealed class GlassSurface : IGlassSurface
    {
        private readonly Window _window;
        private readonly GlassOptions _options;
        private IGlassLayer? _layer;
        private HwndSource? _source;
        private IntPtr _hwnd;
        private bool _disposed;

        public GlassBackdropKind ActiveKind { get; private set; } = GlassBackdropKind.None;

        public GlassSurface(Window window, GlassOptions options)
        {
            _window = window;
            _options = options;

            if (options.Kind == GlassBackdropKind.None)
            {
                return;
            }

            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                Attach(helper.Handle);
            }
            else
            {
                window.SourceInitialized += OnSourceInitialized;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            Attach(new WindowInteropHelper(_window).Handle);
        }

        private void Attach(IntPtr hwnd)
        {
            _hwnd = hwnd;

            _layer = CreateLayer(hwnd, _options);
            if (_layer == null)
            {
                ActiveKind = GlassBackdropKind.None;
                return;
            }

            ActiveKind = _layer.Kind;

            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);

            PushRect();
        }

        /// <summary>按 Kind/Auto 选择实现并尝试初始化，失败自动降级。</summary>
        private static IGlassLayer? CreateLayer(IntPtr hwnd, GlassOptions options)
        {
            if (options.Kind == GlassBackdropKind.SystemBackdrop)
            {
                var l = new SystemBackdropLayer();
                return l.TryInitialize(hwnd, options) ? l : null;
            }

            if (options.Kind == GlassBackdropKind.CompositionBlur)
            {
                var l = new HostBackdropLayer();
                return l.TryInitialize(hwnd, options) ? l : null;
            }

            if (options.Kind == GlassBackdropKind.LegacyBlur)
            {
                var l = new LegacyBlurLayer();
                return l.TryInitialize(hwnd, options) ? l : null;
            }

            // Auto：优先系统背景（Win11 22H2+），其次 Composition HostBackdrop，最后 LegacyBlur。
            IGlassLayer[] candidates =
            {
                new SystemBackdropLayer(),
                new HostBackdropLayer(),
                new LegacyBlurLayer(),
            };

            foreach (var candidate in candidates)
            {
                if (candidate.TryInitialize(hwnd, options))
                {
                    return candidate;
                }

                candidate.Dispose();
            }

            return null;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGED = 0x0047;
            const int WM_SIZE = 0x0005;
            const int WM_MOVE = 0x0003;
            const int WM_DPICHANGED = 0x02E0;
            const int WM_DESTROY = 0x0002;
            const int WM_SHOWWINDOW = 0x0018;

            switch (msg)
            {
                case WM_WINDOWPOSCHANGED:
                case WM_SIZE:
                case WM_MOVE:
                case WM_DPICHANGED:
                    PushRect();
                    break;

                case WM_SHOWWINDOW:
                    if (_layer != null && wParam != IntPtr.Zero)
                    {
                        // 显示时立即回到正确 Z 序
                        _layer.OnOwnerVisibilityChanged(true);
                    }
                    else if (_layer != null && lParam == IntPtr.Zero)
                    {
                        // lParam != 0 表示因被遮挡而隐藏；wParam==0 且 lParam==0 为真正隐藏
                        _layer.OnOwnerVisibilityChanged(false);
                    }
                    break;

                case WM_DESTROY:
                    DisposeCore();
                    break;
            }

            return IntPtr.Zero;
        }

        private void PushRect()
        {
            if (_layer == null || _disposed)
            {
                return;
            }

            if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
            {
                return;
            }

            var deviceRect = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            float radiusDevice = (float)(_options.CornerRadius * GetDpiScale());
            _layer.OnOwnerRectChanged(deviceRect, radiusDevice);
        }

        private double GetDpiScale()
        {
            uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }

        public void UpdateShape(Rect shapeInDip, float cornerRadiusInDip)
        {
            _options.CornerRadius = cornerRadiusInDip;
            PushRect();
        }

        public void SetBlurStrength(double strength)
        {
            _layer?.SetBlurStrength(strength);
        }

        public void SetTint(Color color, double opacity)
        {
            _layer?.SetTint(color, opacity);
        }

        /// <summary>owner 可见性变化（Visibility/WindowState）由门面桥接到层。</summary>
        internal void NotifyVisibility(bool visible)
        {
            _layer?.OnOwnerVisibilityChanged(visible);
        }

        public void Dispose()
        {
            DisposeCore();
        }

        private void DisposeCore()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }

            _layer?.Dispose();
            _layer = null;
            ActiveKind = GlassBackdropKind.None;
        }
    }
}
