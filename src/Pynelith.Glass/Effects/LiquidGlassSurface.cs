using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Pynelith.Glass.Backdrop;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 把液态玻璃效果安装到任意 FrameworkElement。
    /// UI 模块只需一行：
    /// <code>
    /// var glass = LiquidGlassSurface.Install(myPanel, new LiquidGlassSurface.LiquidGlassStyle());
    /// </code>
    ///
    /// 工作原理：
    /// 1. 定时抓取元素背后的屏幕像素（GDI BitBlt，复用缓冲，无每帧分配）；
    /// 2. 作为 <see cref="LiquidGlassEffect"/> 的采样输入，做轻模糊 + 高饱和 + 边缘透镜折射；
    /// 3. 宿主窗口以 WDA_EXCLUDEFROMCAPTURE 排除自身，避免采样自反馈；
    /// 4. 采样不可用时自动降级为「元素自身内容 + 高光/亮边」的玻璃片效果。
    ///
    /// 注意：宿主窗口需 <c>AllowsTransparency="True"</c> 才能让圆角外的桌面透出。
    /// 传入 Panel（Grid/Canvas）时，模块会在其底层注入玻璃层，原有子元素照常显示在玻璃之上；
    /// 传入其他元素时，该元素自身的绘制内容会被玻璃替换（仅剩玻璃外观）。
    /// </summary>
    public sealed class LiquidGlassSurface : IDisposable
    {
        /// <summary>液态玻璃外观参数。</summary>
        public sealed class LiquidGlassStyle
        {
            /// <summary>圆角半径（DIP），与元素尺寸共同决定外形。</summary>
            public double CornerRadius { get; set; } = 20;

            /// <summary>边缘折射带宽度（DIP），越大"玻璃厚度"越明显。</summary>
            public double EdgeBand { get; set; } = 18;

            /// <summary>折射强度 0..1，苹果玻璃约 0.7~1.0。</summary>
            public double Refraction { get; set; } = 0.85;

            /// <summary>背景模糊半径（DIP）。液态玻璃只需轻模糊，背景仍应可辨认。</summary>
            public double BlurRadius { get; set; } = 3.5;

            /// <summary>背景饱和度倍数，&gt;1 提升色彩（苹果玻璃的关键特征）。</summary>
            public double Saturation { get; set; } = 1.55;

            /// <summary>背景亮度倍数。</summary>
            public double Brightness { get; set; } = 1.06;

            /// <summary>玻璃着色。</summary>
            public Color Tint { get; set; } = Color.FromRgb(0xF2, 0xF6, 0xFF);

            /// <summary>着色不透明度，液态玻璃应极淡（0.03~0.10）。</summary>
            public double TintOpacity { get; set; } = 0.06;

            /// <summary>高光强度（镜面亮边 / 边缘内发光 / 顶部柔光）。</summary>
            public double Highlight { get; set; } = 0.85;

            /// <summary>屏幕采样帧率，默认 30；跟随窗口移动足够，且开销很低。</summary>
            public int FramesPerSecond { get; set; } = 30;

            /// <summary>关闭后不做屏幕采样（仅玻璃片外观，无背景折射）。</summary>
            public bool UseScreenBackdrop { get; set; } = true;
        }

        private readonly FrameworkElement _target;
        private readonly FrameworkElement _surface;
        private readonly Border? _injectedLayer;
        private readonly LiquidGlassEffect _effect;
        private readonly LiquidGlassStyle _style;

        private Window? _window;
        private ScreenSampler? _sampler;
        private DispatcherTimer? _timer;
        private bool _displayAffinityApplied;
        private bool _screenBackdropActive;
        private bool _disposed;

        private LiquidGlassSurface(FrameworkElement target, LiquidGlassStyle style)
        {
            _target = target;
            _style = style;

            // ShaderEffect 会用自身输入替换元素的全部绘制内容，
            // 因此当目标是面板（Grid/Canvas 等）时，在底层插入一个专用玻璃层，
            // 面板原有的子元素继续显示在玻璃之上。
            if (target is Panel panel)
            {
                _injectedLayer = new Border
                {
                    // 必须有实际绘制，否则 WPF 不会执行 Effect
                    Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                    IsHitTestVisible = false,
                };
                panel.Children.Insert(0, _injectedLayer);
                _surface = _injectedLayer;
            }
            else
            {
                _surface = target;
            }

            _effect = new LiquidGlassEffect
            {
                // 采样器默认先用元素自身内容；启用屏幕采样后替换为 ImageBrush。
                Input = ShaderEffect.ImplicitInput,
                CornerRadius = style.CornerRadius,
                EdgeBand = style.EdgeBand,
                Refraction = style.Refraction,
                BlurRadius = style.BlurRadius,
                Saturation = style.Saturation,
                Brightness = style.Brightness,
                Tint = style.Tint,
                TintOpacity = style.TintOpacity,
                Highlight = style.Highlight,
                Resolution = new Point(Math.Max(1, _surface.ActualWidth), Math.Max(1, _surface.ActualHeight)),
            };

            _surface.Effect = _effect;
            _surface.SizeChanged += OnTargetSizeChanged;

            if (style.UseScreenBackdrop)
            {
                AttachScreenBackdrop();
            }
        }

        public static LiquidGlassSurface Install(FrameworkElement target, LiquidGlassStyle? style = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return new LiquidGlassSurface(target, style ?? new LiquidGlassStyle());
        }

        /// <summary>动态调整效果参数（须在 UI 线程调用）。</summary>
        public void Update(Action<LiquidGlassEffect> configure)
        {
            configure?.Invoke(_effect);
        }

        /// <summary>当前是否真正在做屏幕背景采样（其余情况为降级模式）。</summary>
        public bool IsScreenBackdropActive => _screenBackdropActive;

        #region 屏幕采样

        private void AttachScreenBackdrop()
        {
            _window = Window.GetWindow(_target);
            if (_window == null)
            {
                // 尚未挂到窗口：等加载完成后再接。
                _target.Loaded += OnTargetLoaded;
                return;
            }

            StartSampling();
        }

        private void OnTargetLoaded(object sender, RoutedEventArgs e)
        {
            _target.Loaded -= OnTargetLoaded;
            if (_disposed)
            {
                return;
            }

            _window = Window.GetWindow(_target);
            if (_window != null)
            {
                StartSampling();
            }
        }

        private void StartSampling()
        {
            if (_window == null)
            {
                return;
            }

            _window.SourceInitialized += OnWindowSourceInitialized;
            _window.IsVisibleChanged += OnWindowIsVisibleChanged;
            _window.StateChanged += OnWindowStateChanged;

            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ApplyDisplayAffinity(hwnd);
            }

            _sampler = new ScreenSampler();
            _effect.Input = _sampler.Brush;

            var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_style.FramesPerSecond, 1, 60));
            _timer = new DispatcherTimer(DispatcherPriority.Render, _target.Dispatcher) { Interval = interval };
            _timer.Tick += OnSampleTick;
            _timer.Start();
        }

        private void OnWindowSourceInitialized(object? sender, EventArgs e)
        {
            if (_window == null)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ApplyDisplayAffinity(hwnd);
            }
        }

        /// <summary>
        /// 把宿主窗口排除出截屏范围。否则 BitBlt 会抓到玻璃窗口自身，
        /// 形成自采样反馈（画面不断放大/发白）。
        /// </summary>
        private void ApplyDisplayAffinity(IntPtr hwnd)
        {
            if (_displayAffinityApplied)
            {
                return;
            }

            _displayAffinityApplied = NativeMethods.SetWindowDisplayAffinity(
                hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

            if (!_displayAffinityApplied)
            {
                // Win10 2004 之前无此能力：关闭采样，避免自反馈，退回玻璃片外观。
                _screenBackdropActive = false;
                _effect.Input = ShaderEffect.ImplicitInput;
                _timer?.Stop();
            }
        }

        private void OnWindowIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e) => UpdateTimerState();

        private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateTimerState();

        private void UpdateTimerState()
        {
            if (_timer == null || _window == null)
            {
                return;
            }

            if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }

        private void OnSampleTick(object? sender, EventArgs e)
        {
            if (_disposed || _sampler == null || _surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
            {
                return;
            }

            try
            {
                var dpi = VisualTreeHelper.GetDpi(_surface);
                Point origin = _surface.PointToScreen(new Point(0, 0));

                int width = Math.Max(1, (int)Math.Round(_surface.ActualWidth * dpi.DpiScaleX));
                int height = Math.Max(1, (int)Math.Round(_surface.ActualHeight * dpi.DpiScaleY));

                _screenBackdropActive = _sampler.Capture(
                    (int)Math.Round(origin.X), (int)Math.Round(origin.Y), width, height);
            }
            catch (InvalidOperationException)
            {
                // 元素已被移出可视化树，停止采样。
                _timer?.Stop();
            }
        }

        #endregion

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => SyncResolution();

        private void SyncResolution()
        {
            _effect.Resolution = new Point(
                Math.Max(1, _surface.ActualWidth),
                Math.Max(1, _surface.ActualHeight));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _surface.SizeChanged -= OnTargetSizeChanged;
            _target.Loaded -= OnTargetLoaded;

            if (_injectedLayer != null && _target is Panel panel)
            {
                panel.Children.Remove(_injectedLayer);
            }

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnSampleTick;
                _timer = null;
            }

            if (_window != null)
            {
                _window.SourceInitialized -= OnWindowSourceInitialized;
                _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
                _window.StateChanged -= OnWindowStateChanged;

                if (_displayAffinityApplied)
                {
                    IntPtr hwnd = new WindowInteropHelper(_window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE);
                    }
                }
            }

            _sampler?.Dispose();
            _sampler = null;

            if (ReferenceEquals(_surface.Effect, _effect))
            {
                _surface.Effect = null;
            }
        }
    }
}
