using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pynelith.Glass.Effects;
using Pynelith.Island.Configuration;
using Pynelith.Island.Services;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 悬浮动态岛主窗口。
    /// 窗口尺寸固定为展开尺寸（分层窗口逐帧改尺寸会触发 DWM 重组，动画发卡顿）；
    /// 真正的"岛"是 CapsuleHost，用阻尼弹簧逐帧插值它的宽高，得到跟手的连续形变。
    /// 玻璃能力全部来自 <see cref="LiquidGlassSurface"/>，本文件不包含任何渲染实现。
    /// </summary>
    public partial class IslandWindow : Window
    {
        private const double SpringZeta = 0.80;   // 阻尼比：<1 带一点回弹，接近系统动效
        private const double MinResponse = 0.12;  // 弹簧响应下限（秒）

        private readonly ConfigService _service;
        private readonly LiquidGlassSurface _glass;
        private readonly MockFeed _feed;
        private readonly DispatcherTimer _clockTimer;
        private readonly RectangleGeometry _capsuleClip = new RectangleGeometry();
        private readonly Stopwatch _springClock = new Stopwatch();

        // 弹簧状态：p 为展开进度 0..1（允许过冲）
        private double _p;
        private double _v;
        private double _target;
        private bool _springRunning;
        private long _lastFrameTicks;

        private bool _expanded;
        private bool _dragging;
        private bool _pressActive;
        private Point _pressPoint;
        private double _lastGlassWidth = -1;
        private double _lastGlassHeight = -1;

        private IslandConfig Config => _service.Config;

        public IslandWindow(ConfigService service)
        {
            _service = service;

            InitializeComponent();

            CapsuleHost.Clip = _capsuleClip;

            // ---- 玻璃：只调用渲染模块公开接口，装在形变胶囊上 ----
            _glass = LiquidGlassSurface.Install(CapsuleHost, new LiquidGlassSurface.LiquidGlassStyle
            {
                CornerRadius = Config.CornerRadius,
                BezelWidth = 22,
                Thickness = 28,
                GlassIor = 1.42,
                Dispersion = 0.05,
                TintAmount = Config.GlassBlurStrength,
                TintTop = Color.FromRgb(6, 8, 11),
                TintBottom = Color.FromRgb(20, 24, 32),
                CaptureMargin = 90,
                LightFollowsCursor = true,
                FramesPerSecond = 30,
            });

            // ---- 模拟数据（接管系统通知时关闭模拟通知） ----
            _feed = new MockFeed(Config, notificationsEnabled: !Config.TakeoverNotifications);
            NotificationList.ItemsSource = _feed.Notifications;
            ExpandedView.DataContext = _feed.Track;

            SetupBridge();

            _clockTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _clockTimer.Tick += (_, _) => UpdateTexts();

            _service.Changed += OnConfigChanged;
            _feed.Notifications.CollectionChanged += (_, _) => UpdateTexts();
            _feed.Track.PropertyChanged += (_, _) => UpdateLyric();

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyWindowSize();
            ApplyMorph(_p);
            UpdateTexts();
            UpdateLyric();
            Reposition();
            _dockGuardActive = true;
            _clockTimer.Start();
        }

        // ---- 停靠位置守护 ----
        // 实测发现：岛压在顶部任务栏上（y=0）时，一旦点击激活，Windows 会由
        // 外部线程对本窗口发起 SetWindowPos，把它推回工作区顶部（work.Top），
        // 表现为"点一下岛就往下偏移"。托管代码无法阻止该调用，只能在
        // WM_WINDOWPOSCHANGING 阶段拦截：只要不是用户拖动，任何位移请求一律
        // 改写回配置停靠位，保证岛永远钉在目标位置。
        private bool _dockGuardActive;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
            {
                src.AddHook(DockGuardWndProc);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private IntPtr DockGuardWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const uint SWP_NOMOVE = 0x0002;

            if (msg == WM_WINDOWPOSCHANGING && _dockGuardActive && !_dragging)
            {
                try
                {
                    var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    if ((wp.flags & SWP_NOMOVE) == 0)
                    {
                        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                        Rect work = SystemParameters.WorkArea;
                        int targetX = (int)Math.Round((work.Left + (work.Width - Width) * 0.5 + Config.ScreenOffsetX) * dpi);
                        int targetY = (int)Math.Round(Config.ScreenOffsetY * dpi);

                        if (wp.x != targetX || wp.y != targetY)
                        {
                            wp.x = targetX;
                            wp.y = targetY;
                            System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
                        }
                    }
                }
                catch (Exception)
                {
                    // 守护失败时放行，不影响正常消息处理。
                }
            }

            return IntPtr.Zero;
        }

        #region 配置

        private void OnConfigChanged(object? sender, EventArgs e)
        {
            if (_takeoverActive != Config.TakeoverNotifications)
            {
                SetupBridge();
            }

            ApplyWindowSize();
            ApplyMorph(_p);
            UpdateTexts();
            Reposition();
        }

        #region 系统通知接管

        private static readonly string[] AccentPalette = { "#7BE0FF", "#4ADE80", "#60A5FA", "#FBBF24", "#F87171", "#C084FC" };

        private SystemNotificationBridge? _bridge;
        private bool _takeoverActive;

        /// <summary>按配置创建/重建通知桥；不可用时回退模拟通知。</summary>
        private void SetupBridge()
        {
            _takeoverActive = Config.TakeoverNotifications;

            _bridge?.Dispose();
            _bridge = null;

            if (!_takeoverActive)
            {
                _feed.SetNotificationsEnabled(true);
                return;
            }

            _bridge = SystemNotificationBridge.TryCreate(true);
            if (_bridge == null)
            {
                _feed.SetNotificationsEnabled(true);
                return;
            }

            _bridge.Received += OnSystemNotification;
            _ = InitializeBridgeAsync(_bridge);
        }

        private async System.Threading.Tasks.Task InitializeBridgeAsync(SystemNotificationBridge bridge)
        {
            bool allowed = await bridge.EnsureAccessAsync();

            if (_bridge != bridge)
            {
                return;
            }

            if (allowed)
            {
                _feed.SetNotificationsEnabled(false);
                bridge.Start();
            }
            else
            {
                // 未授权：先用模拟通知保证界面可用
                _feed.SetNotificationsEnabled(true);
            }
        }

        private void OnSystemNotification(string app, string body)
        {
            if (_feed.IsBlacklisted(app))
            {
                return;
            }

            _feed.Notifications.Insert(0, new Models.NotificationItem
            {
                AppName = app,
                Title = body,
                Accent = AccentFor(app),
                CreatedAt = DateTime.Now,
            });

            while (_feed.Notifications.Count > 3)
            {
                _feed.Notifications.RemoveAt(_feed.Notifications.Count - 1);
            }

            UpdateTexts();
        }

        private static Brush AccentFor(string app)
        {
            int index = Math.Abs(app.GetHashCode(StringComparison.Ordinal)) % AccentPalette.Length;
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(AccentPalette[index]));
        }

        #endregion

        /// <summary>窗口固定为展开尺寸；胶囊在窗口内顶部居中，因此收起态也天然居中。</summary>
        private void ApplyWindowSize()
        {
            double scale = Config.GlobalScale;
            Width = Math.Max(120, Config.ExpandedWidth * scale);
            Height = Math.Max(80, Config.ExpandedHeight * scale);

            ContentScale.ScaleX = scale;
            ContentScale.ScaleY = scale;

            LyricsText.Visibility = Config.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 弹簧形变

        private (double CollapsedW, double CollapsedH, double ExpandedW, double ExpandedH) Metrics()
        {
            double scale = Config.GlobalScale;
            return (
                Config.CollapsedWidth * scale,
                Config.CollapsedHeight * scale,
                Config.ExpandedWidth * scale,
                Config.ExpandedHeight * scale);
        }

        /// <summary>把展开进度 p 应用到胶囊尺寸、圆角裁剪、内容交叉淡入与玻璃参数。</summary>
        private void ApplyMorph(double p)
        {
            double pc = Math.Clamp(p, 0, 1);
            var m = Metrics();

            double width = m.CollapsedW + (m.ExpandedW - m.CollapsedW) * pc;
            double height = m.CollapsedH + (m.ExpandedH - m.CollapsedH) * pc;

            CapsuleHost.Width = width;
            CapsuleHost.Height = height;

            // 设计尺寸（缩放前）= 实际尺寸 / 缩放
            double scale = Math.Max(0.01, Config.GlobalScale);
            ContentHost.Width = width / scale;
            ContentHost.Height = height / scale;

            double radius = Math.Min(Config.CornerRadius * scale, Math.Min(width, height) * 0.5);
            _capsuleClip.Rect = new Rect(0, 0, width, height);
            _capsuleClip.RadiusX = radius;
            _capsuleClip.RadiusY = radius;

            // 内容交叉淡入 + 轻微缩放，避免硬切
            double collapsedAlpha = Math.Clamp(1 - p * 1.9, 0, 1);
            double expandedAlpha = Math.Clamp((p - 0.30) * 1.9, 0, 1);

            CollapsedView.Opacity = collapsedAlpha;
            CollapsedView.IsHitTestVisible = collapsedAlpha > 0.5;
            CollapsedScale.ScaleX = 1 + 0.10 * pc;
            CollapsedScale.ScaleY = 1 + 0.10 * pc;

            ExpandedView.Opacity = expandedAlpha;
            ExpandedView.IsHitTestVisible = expandedAlpha > 0.5;
            double es = 0.92 + 0.08 * Math.Clamp(p, 0, 1.12);
            ExpandedScale.ScaleX = es;
            ExpandedScale.ScaleY = es;

            ApplyGlassShape(width, height);
        }

        /// <summary>把圆角、折射带、染色推给渲染模块（尺寸变化超过阈值才写，避免每帧无谓更新）。</summary>
        private void ApplyGlassShape(double width, double height)
        {
            if (Math.Abs(width - _lastGlassWidth) < 0.4
                && Math.Abs(height - _lastGlassHeight) < 0.4
                && _lastGlassWidth > 0)
            {
                return;
            }

            _lastGlassWidth = width;
            _lastGlassHeight = height;

            double shortSide = Math.Min(width, height);
            double radius = Math.Min(Config.CornerRadius * Config.GlobalScale, shortSide * 0.5);
            double bezel = Math.Min(22 * Config.GlobalScale, shortSide * 0.26);

            _glass.Update(effect =>
            {
                effect.CornerRadius = radius;
                effect.BezelWidth = bezel;
                effect.Thickness = bezel * 1.35;
                effect.TintAmount = Config.GlassBlurStrength;
            });
        }

        private void SetExpanded(bool expanded)
        {
            if (_expanded == expanded)
            {
                return;
            }

            _expanded = expanded;
            _target = expanded ? 1 : 0;

            if (Config.AnimationDurationMs <= 0)
            {
                StopSpring();
                _p = _target;
                _v = 0;
                ApplyMorph(_p);
                UpdateTexts();
                return;
            }

            StartSpring();
            UpdateTexts();
        }

        private void StartSpring()
        {
            if (_springRunning)
            {
                return;
            }

            _springRunning = true;
            _lastFrameTicks = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnSpringFrame;
        }

        private void StopSpring()
        {
            if (!_springRunning)
            {
                return;
            }

            _springRunning = false;
            CompositionTarget.Rendering -= OnSpringFrame;
        }

        private void OnSpringFrame(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = Math.Min(0.032, (now - _lastFrameTicks) / (double)Stopwatch.Frequency);
            _lastFrameTicks = now;

            if (dt <= 0)
            {
                return;
            }

            // 响应时长由配置动画时长决定；阻尼比固定，保证手感一致
            double response = Math.Max(MinResponse, Config.AnimationDurationMs / 1000.0 * 0.55);
            double omega = 2 * Math.PI / response;
            double k = omega * omega;
            double c = 2 * SpringZeta * omega;

            // 半隐式欧拉积分，帧率无关
            double accel = -k * (_p - _target) - c * _v;
            _v += accel * dt;
            _p += _v * dt;

            ApplyMorph(_p);

            if (Math.Abs(_p - _target) < 0.0015 && Math.Abs(_v) < 0.0015)
            {
                _p = _target;
                _v = 0;
                ApplyMorph(_p);
                StopSpring();
            }
        }

        private void ToggleExpanded()
        {
            SetExpanded(!_expanded);
        }

        private void OnCollapseClick(object sender, RoutedEventArgs e)
        {
            SetExpanded(false);
        }

        private SettingsWindow? _settingsWindow;

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_service);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        #endregion

        #region 停靠定位与拖动

        /// <summary>停靠：水平居中于工作区，垂直贴屏幕物理顶（参考图贴顶），再叠加配置偏移。</summary>
        private void Reposition()
        {
            Rect work = SystemParameters.WorkArea;
            double centeredLeft = work.Left + (work.Width - Width) * 0.5;

            Left = Math.Round(centeredLeft + Config.ScreenOffsetX);
            // 贴屏幕物理顶（y=0，压住顶部任务栏，与参考图一致）；不用 work.Top，避免被推下去
            Top = Math.Round(Config.ScreenOffsetY);
        }

        private void PersistScreenOffset()
        {
            Rect work = SystemParameters.WorkArea;
            double centeredLeft = work.Left + (work.Width - Width) * 0.5;

            Config.ScreenOffsetX = Math.Round(Left - centeredLeft);
            Config.ScreenOffsetY = Math.Round(Top);
        }

        private void OnIslandMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractive(e.OriginalSource as DependencyObject))
            {
                return;
            }

            _pressPoint = e.GetPosition(this);
            _pressActive = true;
            _dragging = false;
        }

        private void OnIslandMouseMove(object sender, MouseEventArgs e)
        {
            if (!_pressActive || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point current = e.GetPosition(this);
            // 阈值放宽：手部轻微抖动不应触发拖动，否则点击会把岛拖偏并写回偏移
            bool moved = Math.Abs(current.X - _pressPoint.X) > 20 || Math.Abs(current.Y - _pressPoint.Y) > 20;
            if (!moved)
            {
                return;
            }

            _dragging = true;
            _pressActive = false;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标在拖动开始前已释放：忽略即可。
            }

            PersistScreenOffset();
            _dragging = false;
        }

        private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool isClick = _pressActive && !_dragging;
            _pressActive = false;
            _dragging = false;

            if (isClick)
            {
                ToggleExpanded();
                // 点击完成后强制钉回停靠位：无论此前因何种来源产生过位移，
                // 都保证岛停留在正确的顶部居中目标位置，不出现点击后偏移。
                Reposition();
            }
        }

        private static bool IsInteractive(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase || source is TextBox)
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        #endregion

        #region 文案

        private void UpdateTexts()
        {
            DateTime now = DateTime.Now;
            int count = _feed?.Notifications.Count ?? 0;

            CollapsedTime.Text = now.ToString("HH:mm");
            CollapsedHint.Text = count > 0 ? string.Format("{0} 条新通知", count) : "待机";
            ExpandedTime.Text = now.ToString("HH:mm");
            ExpandedDate.Text = now.ToString("M月d日 dddd");
        }

        private void UpdateLyric()
        {
            string lyric = _feed?.Track.Lyric ?? string.Empty;
            LyricsText.Text = string.IsNullOrEmpty(lyric) ? string.Empty : "♪  " + lyric;
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            StopSpring();
            _clockTimer.Stop();
            _service.Changed -= OnConfigChanged;

            _feed.Dispose();
            _bridge?.Dispose();
            _glass.Dispose();

            base.OnClosed(e);
        }
    }
}
