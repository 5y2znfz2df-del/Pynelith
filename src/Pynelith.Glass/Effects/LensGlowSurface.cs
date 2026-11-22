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
    /// Siri 液态玻璃透镜：把椭圆形折射玻璃安装到任意 FrameworkElement。
    ///
    /// 与 <see cref="LiquidGlassSurface"/> 的区别：
    /// 1. 形状是椭圆，中心带横向色散光带（<see cref="LensGlowEffect"/>）；
    /// 2. 独立成一份采样器，不与胶囊玻璃共享 —— 透镜可以在低配模式下单独关闭折射；
    /// 3. 提供 <see cref="SetState"/> 把「收音 / 生成 / 空闲 / 收拢」映射成光带参数。
    /// </summary>
    public sealed class LensGlowSurface : IDisposable
    {
        /// <summary>透镜外观参数。</summary>
        public sealed class LensStyle
        {
            /// <summary>边缘折射带宽度（DIP）。</summary>
            public double BezelWidth { get; set; } = 30;

            /// <summary>玻璃厚度（DIP），决定折射位移量。</summary>
            public double Thickness { get; set; } = 34;

            /// <summary>折射率。</summary>
            public double GlassIor { get; set; } = 1.45;

            /// <summary>RGB 色散强度。</summary>
            public double Dispersion { get; set; } = 0.06;

            /// <summary>染色强度 0..1。</summary>
            public double TintAmount { get; set; } = 0.32;

            /// <summary>表面不透明度 0..1。</summary>
            public double SurfaceOpacity { get; set; } = 1.0;

            /// <summary>光带半宽 0..1（相对椭圆半宽）。1 = 横贯整个椭圆。</summary>
            public double BandHalfWidth { get; set; } = 1.0;

            /// <summary>光带核心亮度 0..1。</summary>
            public double BandCoreBrightness { get; set; } = 0.88;

            /// <summary>黑白分界位置 0..1：上方黑色玻璃，下方透明玻璃。</summary>
            public double HorizonSplit { get; set; } = 0.5;

            /// <summary>分界过渡宽度 0..1（占玻璃高度的比例）。</summary>
            public double HorizonSoftness { get; set; } = 0.18;

            /// <summary>渐变染色上端（近黑）。</summary>
            public Color TintTop { get; set; } = Color.FromRgb(4, 5, 7);

            /// <summary>渐变染色下端（偏蓝灰）。</summary>
            public Color TintBottom { get; set; } = Color.FromRgb(16, 20, 28);

            /// <summary>抓屏余量（DIP）：边缘折射需要采到玻璃外侧的像素。</summary>
            public double CaptureMargin { get; set; } = 100;

            /// <summary>
            /// 内部是否只输出染色 alpha，让宿主窗口透出真实桌面（录屏兼容模式）。
            /// </summary>
            public bool TransparentInterior { get; set; }

            /// <summary>屏幕采样帧率。透镜静止时不需要高帧率，默认 24。</summary>
            public int FramesPerSecond { get; set; } = 24;

            /// <summary>
            /// 是否启用屏幕折射采样。关闭后透镜退化为「静态玻璃 + 光带」，
            /// 不再抓屏，显存与 CPU 开销大幅下降（低配 GPU 模式）。
            /// </summary>
            public bool UseScreenBackdrop { get; set; } = true;

            /// <summary>是否把宿主窗口排除出截屏/录屏。</summary>
            public bool ExcludeFromCapture { get; set; } = true;
        }

        private readonly FrameworkElement _target;
        private readonly FrameworkElement _surface;
        private readonly Border? _injectedLayer;
        private readonly LensGlowEffect _effect;
        private readonly LensStyle _style;

        private Window? _window;
        private ScreenSampler? _sampler;
        private DispatcherTimer? _timer;
        private bool _displayAffinityApplied;
        private bool _screenBackdropActive;
        private bool _usesScreenBackdrop;
        private bool _disposed;

        private LensGlowSurface(FrameworkElement target, LensStyle style)
        {
            _target = target;
            _style = style;

            if (target is Panel panel)
            {
                // 面板型目标：在底层插入专用玻璃层，原有子元素照常显示在玻璃之上。
                _injectedLayer = new Border
                {
                    Background = style.UseScreenBackdrop
                        ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
                        : CreateStaticGlassBrush(),
                    IsHitTestVisible = false,
                };
                panel.Children.Insert(0, _injectedLayer);
                _surface = _injectedLayer;
            }
            else
            {
                _surface = target;
            }

            _effect = new LensGlowEffect
            {
                Input = ShaderEffect.ImplicitInput,
                BezelWidth = style.BezelWidth,
                Thickness = style.Thickness,
                GlassIor = style.GlassIor,
                Dispersion = style.Dispersion,
                TintAmount = style.TintAmount,
                TintTop = style.TintTop,
                TintBottom = style.TintBottom,
                SurfaceOpacity = style.SurfaceOpacity,
                InteriorTintWeight = style.TransparentInterior ? 1.0 : 0.0,
                HorizonSplit = style.HorizonSplit,
                HorizonSoftness = style.HorizonSoftness,
            };

            _surface.Effect = _effect;
            _surface.SizeChanged += OnTargetSizeChanged;
            _effect.UpdateLayout(_surface.ActualWidth, _surface.ActualHeight, GetDpiScale());

            _usesScreenBackdrop = style.UseScreenBackdrop;
            if (style.UseScreenBackdrop)
            {
                AttachScreenBackdrop();
            }
            else
            {
                if (_injectedLayer != null)
                {
                    _injectedLayer.Background = CreateStaticGlassBrush();
                }

                SyncGeometry();
            }
        }

        public static LensGlowSurface Install(FrameworkElement target, LensStyle? style = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return new LensGlowSurface(target, style ?? new LensStyle());
        }

        /// <summary>动态调整效果参数（须在 UI 线程调用）。</summary>
        public void Update(Action<LensGlowEffect> configure)
        {
            configure?.Invoke(_effect);
            SyncGeometry();
        }

        /// <summary>当前是否真正在做屏幕背景采样。</summary>
        public bool IsScreenBackdropActive => _screenBackdropActive;

        /// <summary>动态调整采样帧率（静止时降到 10fps，动画时回到 24fps）。</summary>
        public void SetFrameRate(int framesPerSecond)
        {
            if (_timer == null)
            {
                return;
            }

            var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(framesPerSecond, 1, 60));
            if (Math.Abs((_timer.Interval - interval).TotalMilliseconds) > 1)
            {
                _timer.Interval = interval;
            }
        }

        /// <summary>
        /// 设置光带状态。
        ///
        /// 注意：光带参数是普通 CLR 属性，改完必须重新推一次着色器常量，
        /// 否则状态变化不会反映到画面上。
        /// </summary>
        /// <param name="intensity">光带总强度 0..1。</param>
        /// <param name="collapse">收拢度 0..1：1 = 完全收拢到中心并变暗。</param>
        /// <param name="reveal">透镜展开度 0..1。</param>
        public void SetState(double intensity, double collapse, double reveal)
        {
            _effect.BandIntensity = Math.Clamp(intensity, 0, 1);
            _effect.BandCollapse = Math.Clamp(collapse, 0, 1);
            _effect.Reveal = Math.Clamp(reveal, 0, 1);
            SyncGeometry();

            // 透镜完全收起时没必要继续抓屏：采样定时器跟着状态一起停 / 起。
            if (_timer != null)
            {
                bool expanded = _effect.Reveal >= 0.01;
                bool canSample = _window is { IsVisible: true }
                    && _window.WindowState != WindowState.Minimized;

                if (expanded && canSample)
                {
                    _timer.Start();
                }
                else
                {
                    _timer.Stop();
                }
            }
        }

        #region 屏幕采样

        private void AttachScreenBackdrop()
        {
            _window = Window.GetWindow(_target);
            if (_window == null)
            {
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
            if (hwnd != IntPtr.Zero && _style.ExcludeFromCapture)
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
            if (hwnd != IntPtr.Zero && _style.ExcludeFromCapture)
            {
                ApplyDisplayAffinity(hwnd);
            }
        }

        /// <summary>
        /// 把宿主窗口排除出截屏范围，避免 BitBlt 抓到玻璃窗口自身形成自采样反馈。
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

                Int32Rect mask = _style.ExcludeFromCapture
                    ? new Int32Rect(0, 0, 0, 0)
                    : new Int32Rect(margin, margin, width, height);

                _effect.BackdropOffset = new Point(margin, margin);
                _effect.BackdropSize = new Point(width + margin * 2, height + margin * 2);

                _sampler.Request(
                    (int)Math.Round(origin.X) - margin,
                    (int)Math.Round(origin.Y) - margin,
                    width + margin * 2,
                    height + margin * 2,
                    mask);

                if (_sampler.TryUpload())
                {
                    _screenBackdropActive = true;
                }
            }
            catch (InvalidOperationException)
            {
                _timer?.Stop();
            }
        }

        #endregion

        /// <summary>非采样模式的玻璃本体：斜向冷灰 → 近黑 → 暖灰。</summary>
        private static Brush CreateStaticGlassBrush()
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x22, 0x28, 0x36), 0.0),
                    new GradientStop(Color.FromRgb(0x11, 0x13, 0x1B), 0.45),
                    new GradientStop(Color.FromRgb(0x07, 0x08, 0x0C), 0.75),
                    new GradientStop(Color.FromRgb(0x18, 0x10, 0x14), 1.0),
                },
            };
        }

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => SyncGeometry();

        private void SyncGeometry()
        {
            _effect.UpdateLayout(_surface.ActualWidth, _surface.ActualHeight, GetDpiScale());

            if (!_usesScreenBackdrop)
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

                if (_window != null)
                {
                    _window.SourceInitialized -= OnWindowSourceInitialized;
                    _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
                    _window.StateChanged -= OnWindowStateChanged;
                }
            }

            _sampler?.Dispose();
            _sampler = null;
            _effect.Input = ShaderEffect.ImplicitInput;
            _surface.Effect = null;
        }
    }
}
