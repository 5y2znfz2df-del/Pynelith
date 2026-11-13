using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        /// <summary>液态玻璃外观参数（默认值取自 liquid-glass-rs 的番茄钟玻璃）。</summary>
        public sealed class LiquidGlassStyle
        {
            /// <summary>圆角半径（DIP）。</summary>
            public double CornerRadius { get; set; } = 46;

            /// <summary>边缘折射带宽度（DIP），越大折射范围越宽。</summary>
            public double BezelWidth { get; set; } = 26;

            /// <summary>玻璃厚度（DIP），决定折射位移量。</summary>
            public double Thickness { get; set; } = 34;

            /// <summary>折射率，默认 1.42。</summary>
            public double GlassIor { get; set; } = 1.42;

            /// <summary>RGB 色散。</summary>
            public double Dispersion { get; set; } = 0.05;

            /// <summary>染色强度 0..1，黑玻璃约 0.70。</summary>
            public double TintAmount { get; set; } = 0.70;

            /// <summary>
            /// 表面整体不透明度 0..1，默认 1（采样模式）。
            /// 不做屏幕采样的可用录制模式建议 0.86–0.92，让桌面透出来。
            /// </summary>
            public double SurfaceOpacity { get; set; } = 1.0;

            /// <summary>渐变染色上端（近黑）。</summary>
            public Color TintTop { get; set; } = Color.FromRgb(5, 6, 8);

            /// <summary>渐变染色下端（偏蓝灰）。</summary>
            public Color TintBottom { get; set; } = Color.FromRgb(24, 28, 36);

            /// <summary>抓屏余量（DIP）：边缘折射需要采到玻璃外侧的像素。</summary>
            public double CaptureMargin { get; set; } = 120;

            /// <summary>光源是否跟随鼠标（跟手效果）。</summary>
            public bool LightFollowsCursor { get; set; } = true;

            /// <summary>屏幕采样帧率，默认 30；跟随窗口移动足够。</summary>
            public int FramesPerSecond { get; set; } = 30;

            /// <summary>关闭后不做屏幕采样（仅玻璃片外观，无背景折射）。</summary>
            public bool UseScreenBackdrop { get; set; } = true;

            /// <summary>
            /// 是否把宿主窗口排除出截屏/录屏。
            /// 默认 true —— 让屏幕采样抓不到自己，避免自反馈。
            /// 若需要窗口出现在远程桌面/串流/录屏画面中，必须设为 false；
            /// 此时屏幕采样会自动关闭（否则会抓到自己），退化为半透明玻璃外观。
            /// </summary>
            public bool ExcludeFromCapture { get; set; } = true;
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
                    // 必须有实际绘制，否则 WPF 不会执行 Effect。
                    // 采样模式下这层只是占位；非采样模式用深黑灰纵向渐变当玻璃本体，
                    // 让录屏/截图里的岛也有体积感，而不是一块死黑。
                    Background = style.UseScreenBackdrop && style.ExcludeFromCapture
                        ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
                        : new LinearGradientBrush
                        {
                            StartPoint = new Point(0, 0),
                            EndPoint = new Point(0, 1),
                            GradientStops =
                            {
                                new GradientStop(Color.FromRgb(0x23, 0x26, 0x2C), 0.0),
                                new GradientStop(Color.FromRgb(0x12, 0x14, 0x18), 0.5),
                                new GradientStop(Color.FromRgb(0x07, 0x08, 0x0A), 1.0),
                            },
                        },
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
                BezelWidth = style.BezelWidth,
                Thickness = style.Thickness,
                GlassIor = style.GlassIor,
                Dispersion = style.Dispersion,
                TintAmount = style.TintAmount,
                TintTop = style.TintTop,
                TintBottom = style.TintBottom,
                SurfaceOpacity = style.SurfaceOpacity,
            };

            _surface.Effect = _effect;
            _surface.SizeChanged += OnTargetSizeChanged;
            _effect.UpdateLayout(_surface.ActualWidth, _surface.ActualHeight, GetDpiScale());

            // 关闭截屏排除时不能做屏幕采样（会抓到自身形成反馈），走半透明玻璃外观。
            if (style.UseScreenBackdrop && style.ExcludeFromCapture)
            {
                AttachScreenBackdrop();
            }
            else
            {
                // 无采样：用半透明深色渐变代替背景，保留轮廓与高光。
                if (_injectedLayer != null)
                {
                    _injectedLayer.Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(0, 1),
                        GradientStops =
                        {
                            new GradientStop(Color.FromRgb(0x23, 0x26, 0x2C), 0.0),
                            new GradientStop(Color.FromRgb(0x12, 0x14, 0x18), 0.5),
                            new GradientStop(Color.FromRgb(0x07, 0x08, 0x0A), 1.0),
                        },
                    };
                }

                _effect.TintAmount = 0.0;
                SyncGeometry();
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
            SyncGeometry();
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
                int margin = Math.Max(1, (int)Math.Round(_style.CaptureMargin * dpi.DpiScaleX));

                // 四周多抓一圈：边缘折射要采到玻璃外侧的像素，窗口移动时也不会出现错位切片。
                _screenBackdropActive = _sampler.Capture(
                    (int)Math.Round(origin.X) - margin,
                    (int)Math.Round(origin.Y) - margin,
                    width + margin * 2,
                    height + margin * 2);

                if (_screenBackdropActive)
                {
                    _effect.BackdropOffset = new Point(margin, margin);
                    _effect.BackdropSize = new Point(width + margin * 2, height + margin * 2);
                }

                UpdateLightAngle();
            }
            catch (InvalidOperationException)
            {
                // 元素已被移出可视化树，停止采样。
                _timer?.Stop();
            }
        }

        /// <summary>光源方位 = 光标相对玻璃中心的方位角（跟手高光）。</summary>
        private void UpdateLightAngle()
        {
            if (!_style.LightFollowsCursor)
            {
                return;
            }

            Point cursor = Mouse.GetPosition(_surface);
            double dx = cursor.X - _surface.ActualWidth * 0.5;
            double dy = cursor.Y - _surface.ActualHeight * 0.5;
            _effect.LightAngle = Math.Atan2(dy, dx);
        }

        #endregion

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => SyncGeometry();

        private void SyncGeometry()
        {
            _effect.UpdateLayout(_surface.ActualWidth, _surface.ActualHeight, GetDpiScale());

            // 无屏幕采样时，输入就是元素自身的渐变：按 1:1 采样，
            // 否则默认的抓屏尺寸/偏移会把渐变映射到错误的位置。
            if (!_screenBackdropActive)
            {
                double scale = GetDpiScale();
                _effect.BackdropOffset = new Point(0, 0);
                _effect.BackdropSize = new Point(
                    Math.Max(1, _surface.ActualWidth * scale),
                    Math.Max(1, _surface.ActualHeight * scale));
            }
        }

        private double GetDpiScale()
        {
            try
            {
                return VisualTreeHelper.GetDpi(_surface).DpiScaleX;
            }
            catch (InvalidOperationException)
            {
                return 1.0;
            }
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
