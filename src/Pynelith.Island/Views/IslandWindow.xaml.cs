using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pynelith.Glass.Effects;
using Pynelith.Island.Configuration;
using Pynelith.Island.Services;
using Pynelith.Island.Services.Lyrics;
using Pynelith.Island.Services.Media;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 悬浮动态岛主窗口。
    /// 两种自动形态（无手动展开交互）：
    /// 空闲态 —— 无媒体播放时显示时间胶囊；
    /// 媒体态 —— SMTC 扫描到播放会话后直显专辑封面、曲目与逐行歌词，并提供播放控制。
    /// 窗口尺寸固定为媒体态尺寸（分层窗口逐帧改尺寸会触发 DWM 重组，动画发卡顿）；
    /// 真正的"岛"是 CapsuleHost，用加固的阻尼弹簧逐帧插值它的宽高。
    /// 玻璃能力全部来自 <see cref="LiquidGlassSurface"/>，本文件不包含任何渲染实现。
    /// </summary>
    public partial class IslandWindow : Window
    {
        private const double SpringZeta = 0.92;    // 阻尼比：接近临界阻尼，收尾干脆不过冲
        private const double MinResponse = 0.12;   // 弹簧响应下限（秒）
        private const double MaxFrameDelta = 0.05; // 单帧最大步长（秒），防止时间跳变导致的位置突跳

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private readonly ConfigService _service;
        private readonly MediaSessionService _media;
        private readonly LyricsService _lyrics;
        private readonly LiquidGlassSurface _glass;
        private readonly MockFeed _feed;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _lyricTimer;
        private readonly DispatcherTimer _topmostTimer;
        private readonly RectangleGeometry _capsuleClip = new RectangleGeometry();

        // 弹簧状态：p 为媒体态进度 0..1（允许过冲）
        private double _p;
        private double _v;
        private double _target;
        private bool _springRunning;
        private long _lastFrameTicks;

        private bool _mediaMode;
        private bool _dragging;
        private bool _pressActive;
        private Point _pressPoint;
        private double _lastGlassWidth = -1;
        private double _lastGlassHeight = -1;
        private int _glassFrameSkip;
        private long _springStartTicks;

        // 详情展开弹簧（与主弹簧共用参数）：_d 为展开度 0..1
        private bool _detail;
        private double _d;
        private double _dv;

        // 点击播放后短暂锁定图标，避免 SMTC 状态回报滞后把图标刷回旧值
        private DateTime _playGlyphLockUntil = DateTime.MinValue;

        private IntPtr _hwnd;
        private bool _dockGuardActive;

        private string _trackFingerprint = string.Empty;
        private LrcDocument? _currentLrc;
        private int _lyricIndex = -2; // -2 表示尚未同步过
        private TimeSpan _lastLyricPosition = TimeSpan.MinValue;
        private CancellationTokenSource? _lyricLookupCts;
        private bool _lyricShowingA;
        private string _currentLyricText = string.Empty;

        private IslandConfig Config => _service.Config;

        /// <summary>定时器回调保护：单次歌词/时钟异常不应直接崩溃整个宿主。</summary>
        private static void Guarded(Action action)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // 忽略单次取样异常，下一拍继续。
            }
        }

        public IslandWindow(ConfigService service, MediaSessionService media, LyricsService lyrics)
        {
            _service = service;
            _media = media;
            _lyrics = lyrics;

            InitializeComponent();

            CapsuleHost.Clip = _capsuleClip;

            // ---- 玻璃：只调用渲染模块公开接口，装在形变胶囊上（60fps 采样匹配动画帧率） ----
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
                CaptureMargin = 50,
                LightFollowsCursor = true,
                // 抓屏在 UI 线程执行，是逐帧动画里最重的一步；玻璃只是背景模糊，
                // 24fps 采样肉眼无差别，抓屏面积也一并收窄，给形变动画腾出 CPU。
                FramesPerSecond = 24,
            });

            // ---- 通知仅用于空闲态计数（接管系统通知时关闭模拟通知） ----
            _feed = new MockFeed(Config, notificationsEnabled: !Config.TakeoverNotifications);
            SetupBridge();

            _clockTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _clockTimer.Tick += (_, _) => Guarded(() =>
            {
                UpdateTexts();
                // 兜底：即使歌词定时器因极端负载被推迟，也保证每秒至少对齐一次
                SyncLyricPosition();
            });

            _lyricTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                // 30ms 采样：切行延迟不超过一帧多一点，肉眼与歌词时间戳一致。
                // 优先级用 Normal：Render 优先级会被逐帧动画长期抢占，导致歌词长时间不更新。
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _lyricTimer.Tick += (_, _) => Guarded(() =>
            {
                SyncLyricPosition();
                SyncPlaybackGlyph();
            });

            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            _topmostTimer.Tick += (_, _) => ReassertTopmost();

            _service.Changed += OnConfigChanged;
            _feed.Notifications.CollectionChanged += (_, _) => UpdateTexts();
            _media.StateChanged += OnMediaStateChanged;
            _lyrics.ManualImported += OnLyricsManualImported;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyWindowSize();
            ApplyMorph(_p);
            UpdateTexts();
            Reposition();
            _dockGuardActive = true;

            _clockTimer.Start();
            _lyricTimer.Start();
            _topmostTimer.Start();

            // 启动即同步一次媒体状态（会话服务可能已先就绪）
            OnMediaStateChanged(this, EventArgs.Empty);
        }

        #region 停靠守护与置顶

        // 实测发现两类外部搬动：
        // 1) 岛压在顶部任务栏上（y=0）时被点击激活，Windows 会由外部线程发起
        //    SetWindowPos 把它推回工作区顶部（work.Top）；
        // 2) 其它窗口激活时，系统可能把岛的 Z 序压下去导致被遮挡。
        // 两者都在 WM_WINDOWPOSCHANGING 阶段拦截：非拖动时，位置一律改写回
        // 配置停靠位，Z 序插入点一律强制 HWND_TOPMOST，保证岛永远钉在最上层。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource src)
            {
                _hwnd = src.Handle;
                src.AddHook(DockGuardWndProc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
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

            if (msg == WM_WINDOWPOSCHANGING && _dockGuardActive && !_dragging)
            {
                try
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    bool changed = false;

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
                            changed = true;
                        }
                    }

                    if ((wp.flags & SWP_NOZORDER) == 0 && wp.hwndInsertAfter != HwndTopmost)
                    {
                        wp.hwndInsertAfter = HwndTopmost;
                        changed = true;
                    }

                    if (changed)
                    {
                        Marshal.StructureToPtr(wp, lParam, false);
                    }
                }
                catch (Exception)
                {
                    // 守护失败时放行，不影响正常消息处理。
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>周期性重申置顶：即使被外部强行压下去，也会在一个心跳内回到最上层。</summary>
        private void ReassertTopmost()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible)
            {
                return;
            }

            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        #endregion

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

            // 歌词开关打开时对当前曲目重新匹配
            if (Config.LyricsEnabled && _mediaMode && !string.IsNullOrWhiteSpace(_media.Title))
            {
                BeginLyricLookup(_media.Title, _media.Artist);
            }
        }

        #region 系统通知接管

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

        private async Task InitializeBridgeAsync(SystemNotificationBridge bridge)
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

        private static readonly string[] AccentPalette = { "#7BE0FF", "#4ADE80", "#60A5FA", "#FBBF24", "#F87171", "#C084FC" };

        private static Brush AccentFor(string app)
        {
            int index = Math.Abs(app.GetHashCode(StringComparison.Ordinal)) % AccentPalette.Length;
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(AccentPalette[index]));
        }

        #endregion

        /// <summary>窗口固定为最大形态尺寸（详情展开态）；胶囊按当前形态在窗口内顶部居中。</summary>
        private void ApplyWindowSize()
        {
            double scale = Config.GlobalScale;
            var m = Metrics();

            Width = Math.Max(m.DetailW, m.MediaW);
            Height = Math.Max(m.DetailH, m.IdleH);

            ContentScale.ScaleX = scale;
            ContentScale.ScaleY = scale;

            Visibility lyricVisibility = Config.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
            CompactLyricHost.Visibility = lyricVisibility;
            DetailLyricHost.Visibility = lyricVisibility;
        }

        #endregion

        #region 弹簧形变（加固版）

        /// <summary>
        /// 三种形态：空闲（时间胶囊）→ 媒体紧凑（封面+歌词）→ 展开（大封面+曲目信息+歌词）。
        /// 紧凑态高度与空闲态一致；只有展开态更高。
        /// </summary>
        private (double IdleW, double IdleH, double MediaW, double MediaH, double DetailW, double DetailH) Metrics()
        {
            double scale = Config.GlobalScale;
            double idleHeight = Config.CollapsedHeight * scale;
            double mediaWidth = Config.ExpandedWidth * scale;

            return (
                Config.CollapsedWidth * scale,
                idleHeight,
                mediaWidth,
                idleHeight,
                mediaWidth + 60 * scale,
                Math.Max(60 * scale, idleHeight));
        }

        /// <summary>
        /// 把形态进度应用到胶囊尺寸与内容交叉淡入。
        /// 关键：内容层是固定尺寸的，动画帧内只改容器尺寸与 Opacity，
        /// 不做任何布局重算/字号变更，因此展开过程不会卡顿或抖动。
        /// </summary>
        private void ApplyMorph(double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p))
            {
                p = _target;
                _v = 0;
            }

            if (double.IsNaN(_d) || double.IsInfinity(_d))
            {
                _d = _detail ? 1 : 0;
                _dv = 0;
            }

            double pc = Math.Clamp(p, 0, 1);
            double pd = Math.Clamp(_d, 0, 1) * pc; // 展开只在媒体态生效
            var m = Metrics();

            double width = m.IdleW + (m.MediaW - m.IdleW) * pc + (m.DetailW - m.MediaW) * pd;
            double height = m.IdleH + (m.MediaH - m.IdleH) * pc + (m.DetailH - m.MediaH) * pd;

            CapsuleHost.Width = width;
            CapsuleHost.Height = height;

            double scale = Math.Max(0.01, Config.GlobalScale);
            ContentHost.Width = width / scale;
            ContentHost.Height = height / scale;

            double radius = Math.Min(Config.CornerRadius * scale, Math.Min(width, height) * 0.5);
            _capsuleClip.Rect = new Rect(0, 0, width, height);
            _capsuleClip.RadiusX = radius;
            _capsuleClip.RadiusY = radius;

            // 三层交叉淡入：空闲 ⇄ 紧凑 ⇄ 展开
            double idleAlpha = Math.Clamp(1 - pc * 1.9, 0, 1);
            double compactAlpha = Math.Clamp(pc * 2.2, 0, 1) * Math.Clamp(1 - pd * 1.8, 0, 1);
            double detailAlpha = Math.Clamp((pd - 0.18) / 0.82, 0, 1);

            IdleView.Opacity = idleAlpha;
            IdleView.IsHitTestVisible = idleAlpha > 0.5;
            IdleScale.ScaleX = 1 + 0.08 * pc;
            IdleScale.ScaleY = 1 + 0.08 * pc;

            CompactLayer.Opacity = compactAlpha;
            CompactLayer.IsHitTestVisible = compactAlpha > 0.5;

            DetailLayer.Opacity = detailAlpha;
            DetailLayer.IsHitTestVisible = detailAlpha > 0.5;

            ApplyGlassShape(width, height);
        }

        /// <summary>
        /// 把圆角、折射带、染色推给渲染模块。
        /// 阈值 2.5 DIP：动画期间把玻璃重绘次数从「每帧」降到约 1/4，
        /// 玻璃是背景模糊，2.5 DIP 内的形状差异肉眼不可见，但省下大量抓屏开销。
        /// </summary>
        private void ApplyGlassShape(double width, double height)
        {
            // 动画期间每 5 帧才真正刷新玻璃：一次刷新包含抓屏 + 像素处理 + 位图上传，
            // 是逐帧动画里最重的一步。降到约 12Hz 后，慢帧（>16.7ms）数量显著下降，
            // 而玻璃是背景模糊、形状差异在 2.5 DIP 内不可见，观感无损失。
            if (_springRunning && (++_glassFrameSkip % 5) != 0)
            {
                return;
            }

            if (Math.Abs(width - _lastGlassWidth) < 2.5
                && Math.Abs(height - _lastGlassHeight) < 2.5
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

        /// <summary>
        /// 启动一次形变动画。加固点（修复"跳变/无动画"）：
        /// 每次启动都重新同步帧时钟，避免陈旧时间戳造成首帧大步长；
        /// 目标未变且已到位时直接落位；动画时长为 0 时立即落位不走弹簧。
        /// </summary>
        private void AnimateMorphTo(double target)
        {
            _target = Math.Clamp(target, 0, 1);

            if (Config.AnimationDurationMs <= 0 || Math.Abs(_p - _target) < 0.0005)
            {
                _p = _target;
                _v = 0;
                _d = _detail ? 1 : 0;
                _dv = 0;
                StopSpring();
                ApplyMorph(_p);
                return;
            }

            EnsureSpring();
        }

        /// <summary>切换详情展开/收起（左键点击胶囊触发）。</summary>
        private void SetDetail(bool detail)
        {
            if (_detail == detail)
            {
                return;
            }

            _detail = detail;

            if (Config.AnimationDurationMs <= 0)
            {
                _d = detail ? 1 : 0;
                _dv = 0;
                ApplyMorph(_p);
                return;
            }

            EnsureSpring();
        }

        private void EnsureSpring()
        {
            _lastFrameTicks = Stopwatch.GetTimestamp();
            _springStartTicks = _lastFrameTicks;
            if (!_springRunning)
            {
                _springRunning = true;
                CompositionTarget.Rendering += OnSpringFrame;
            }
        }

        private void StopSpring()
        {
            if (!_springRunning)
            {
                return;
            }

            _springRunning = false;
            CompositionTarget.Rendering -= OnSpringFrame;

            // 让动画结束后的下一次 ApplyMorph 一定把最终形状推给玻璃层
            _lastGlassWidth = -1;
            _lastGlassHeight = -1;
            _glassFrameSkip = 0;
        }

        private void OnSpringFrame(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastFrameTicks) / (double)Stopwatch.Frequency;
            _lastFrameTicks = now;

            // 帧步长钳制：休眠恢复/帧率抖动不会产生位置突跳
            dt = Math.Clamp(dt, 0, MaxFrameDelta);
            if (dt <= 0)
            {
                return;
            }

            // 响应时长由配置动画时长决定；阻尼比固定，保证手感一致
            double response = Math.Max(MinResponse, Config.AnimationDurationMs / 1000.0 * 0.5);
            double omega = 2 * Math.PI / response;
            double k = omega * omega;
            double c = 2 * SpringZeta * omega;

            // 固定子步长积分：帧长可能达到 50ms（玻璃抓屏较重），
            // 单步欧拉在 ω·dt 接近 2 时会振荡甚至发散，表现为抖动且永不收敛。
            // 拆成 ≤8ms 的子步后，任何帧率下轨迹一致且必然收敛。
            const double maxSubStep = 0.008;
            int steps = Math.Min(8, Math.Max(1, (int)Math.Ceiling(dt / maxSubStep)));
            double h = dt / steps;

            double detailTarget = _detail ? 1 : 0;

            for (int i = 0; i < steps; i++)
            {
                double accel = -k * (_p - _target) - c * _v;
                _v += accel * h;
                _p += _v * h;

                double dAccel = -k * (_d - detailTarget) - c * _dv;
                _dv += dAccel * h;
                _d += _dv * h;
            }

            bool mainSettled = Math.Abs(_p - _target) < 0.0015 && Math.Abs(_v) < 0.0015;
            bool detailSettled = Math.Abs(_d - detailTarget) < 0.0015 && Math.Abs(_dv) < 0.0015;

            // 超时兜底：极端负载下也不允许动画无限挂起，否则 Rendering 会持续占满 UI 线程
            bool timedOut = Stopwatch.GetElapsedTime(_springStartTicks) > TimeSpan.FromSeconds(2);

            if (mainSettled && detailSettled || timedOut)
            {
                _p = _target;
                _v = 0;
                _d = detailTarget;
                _dv = 0;
                StopSpring();
                ApplyMorph(_p);
                return;
            }

            ApplyMorph(_p);
        }

        /// <summary>空闲/媒体形态切换（由媒体会话状态自动驱动）；离开媒体态时收起详情。</summary>
        private void SetMediaMode(bool media)
        {
            if (_mediaMode == media)
            {
                return;
            }

            _mediaMode = media;

            if (!media)
            {
                _detail = false;
            }

            AnimateMorphTo(media ? 1 : 0);
            UpdateTexts();
        }

        private SettingsWindow? _settingsWindow;

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_service, _lyrics, _media);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        private void OnExit(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        #endregion

        #region 媒体状态与歌词

        private void OnMediaStateChanged(object? sender, EventArgs e)
        {
            string fingerprint = _media.Title + "\u0001" + _media.Artist;
            if (fingerprint != _trackFingerprint)
            {
                _trackFingerprint = fingerprint;
                OnTrackChanged();
            }

            SyncPlaybackGlyph();
            CompactCoverImage.Source = _media.Artwork;
            DetailCoverImage.Source = _media.Artwork;
            Visibility fallbackVisibility = _media.Artwork == null ? Visibility.Visible : Visibility.Collapsed;
            CompactCoverFallback.Visibility = fallbackVisibility;
            DetailCoverFallback.Visibility = fallbackVisibility;

            TrackLabel.Text = string.IsNullOrWhiteSpace(_media.Artist)
                ? _media.Title
                : _media.Title + " · " + _media.Artist;

            bool media = _media.HasSession && !string.IsNullOrWhiteSpace(_media.Title);
            SetMediaMode(media);
        }

        /// <summary>把播放/暂停图标同步为当前状态；点击后的短暂窗口内不覆盖乐观值。</summary>
        private void SyncPlaybackGlyph()
        {
            if (DateTime.UtcNow < _playGlyphLockUntil)
            {
                return;
            }

            string glyph = _media.IsPlaying ? "⏸" : "▶";
            if (!Equals(PlayButton.Content, glyph))
            {
                PlayButton.Content = glyph;
            }
        }

        private void OnTrackChanged()
        {
            _currentLrc = null;
            _lyricIndex = -2;

            if (string.IsNullOrWhiteSpace(_media.Title))
            {
                SetLyricImmediate(string.Empty);
                return;
            }

            if (!Config.LyricsEnabled)
            {
                SetLyricImmediate(string.Empty);
                return;
            }

            SetLyricImmediate("正在匹配歌词…");
            BeginLyricLookup(_media.Title, _media.Artist);
        }

        private void BeginLyricLookup(string title, string artist)
        {
            _lyricLookupCts?.Cancel();
            var cts = new CancellationTokenSource();
            _lyricLookupCts = cts;

            _ = LookupLyricsAsync(title, artist, cts.Token);
        }

        private async Task LookupLyricsAsync(string title, string artist, CancellationToken ct)
        {
            try
            {
                LrcDocument doc = await _lyrics.GetLyricsAsync(title, artist, _media.Duration, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _currentLrc = doc;
                _lyricIndex = -2;

                if (doc.IsEmpty)
                {
                    SetLyricImmediate("暂无歌词");
                }
                else
                {
                    SyncLyricPosition();
                }
            }
            catch (OperationCanceledException)
            {
                // 曲目已切换，本次结果作废。
            }
            catch (Exception)
            {
                if (!ct.IsCancellationRequested)
                {
                    SetLyricImmediate("歌词获取失败");
                }
            }
        }

        private void OnLyricsManualImported(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_mediaMode && !string.IsNullOrWhiteSpace(_media.Title))
                {
                    BeginLyricLookup(_media.Title, _media.Artist);
                }
            });
        }

        /// <summary>
        /// 按播放位置同步当前歌词行（50ms 采样 + 110ms 提前量，对应切行动画的一半时长）。
        /// 位置发生跳变（切歌/拖动进度）时强制重算，避免沿用旧行号。
        /// </summary>
        private void SyncLyricPosition()
        {
            if (!_mediaMode || !Config.LyricsEnabled)
            {
                return;
            }

            LrcDocument? doc = _currentLrc;
            if (doc == null || doc.IsEmpty)
            {
                return;
            }

            TimeSpan position = _media.Position;

            // 注意：TimeSpan.MinValue 取 Duration 会溢出，必须先排除首次采样
            if (_lastLyricPosition != TimeSpan.MinValue
                && (position - _lastLyricPosition).Duration() > TimeSpan.FromSeconds(1.5))
            {
                _lyricIndex = -2; // 位置跳变：强制重新定位
            }

            _lastLyricPosition = position;

            // 严格按时间戳切换（不再提前）：提前量会让短间隔的句子"抢跑"到下一句。
            // 用户可用 LyricOffsetMs 微调整首歌词的整体偏移（正值 = 歌词提前显示）。
            int index = doc.LineAt(position + TimeSpan.FromMilliseconds(Config.LyricOffsetMs));
            if (index == _lyricIndex)
            {
                return;
            }

            _lyricIndex = index;
            string text = index >= 0 ? doc.Lines[index].Text : string.Empty;

            // 连续多行文本相同（副歌重复）时不重播滚动动画，否则会看起来像卡住/抽搐
            if (!string.Equals(text, _currentLyricText, StringComparison.Ordinal))
            {
                _currentLyricText = text;
                SwapLyric(text);
            }
        }

        /// <summary>歌词行交叉淡入切换：紧凑层与展开层同步切换（纯合成层动画，稳定 60fps）。</summary>
        private void SwapLyric(string text)
        {
            // _lyricShowingA 表示"当前可见的是 A"：A 可见时把新行写进 B
            bool toB = _lyricShowingA;
            _lyricShowingA = !toB;

            AnimateLyricRow(
                toB ? CompactLyricB : CompactLyricA,
                toB ? CompactLyricA : CompactLyricB,
                toB ? CompactLyricBTrans : CompactLyricATrans,
                toB ? CompactLyricATrans : CompactLyricBTrans,
                text);

            AnimateLyricRow(
                toB ? DetailLyricB : DetailLyricA,
                toB ? DetailLyricA : DetailLyricB,
                toB ? DetailLyricBTrans : DetailLyricATrans,
                toB ? DetailLyricATrans : DetailLyricBTrans,
                text);
        }

        private static void AnimateLyricRow(
            TextBlock incoming, TextBlock outgoing,
            TranslateTransform inTrans, TranslateTransform outTrans, string text)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            const double durationMs = 220;

            if (string.IsNullOrWhiteSpace(text))
            {
                outgoing.BeginAnimation(OpacityProperty, Fade(outgoing.Opacity, 0, durationMs, ease));
                outTrans.BeginAnimation(TranslateTransform.YProperty, Fade(outTrans.Y, -10, durationMs, ease));
                incoming.BeginAnimation(OpacityProperty, null);
                incoming.Text = string.Empty;
                incoming.Opacity = 0;
                return;
            }

            incoming.Text = text;
            inTrans.Y = 10;
            incoming.BeginAnimation(OpacityProperty, Fade(0, 1, durationMs, ease));
            inTrans.BeginAnimation(TranslateTransform.YProperty, Fade(10, 0, durationMs, ease));

            outgoing.BeginAnimation(OpacityProperty, Fade(outgoing.Opacity, 0, durationMs, ease));
            outTrans.BeginAnimation(TranslateTransform.YProperty, Fade(outTrans.Y, -10, durationMs, ease));
        }

        /// <summary>无动画直接设置当前可见歌词文本（清空/占位文案用）。</summary>
        private void SetLyricImmediate(string text)
        {
            _currentLyricText = text;

            SetLyricRowImmediate(CompactLyricA, CompactLyricB,
                CompactLyricATrans, CompactLyricBTrans, text);
            SetLyricRowImmediate(DetailLyricA, DetailLyricB,
                DetailLyricATrans, DetailLyricBTrans, text);
        }

        private void SetLyricRowImmediate(
            TextBlock a, TextBlock b, TranslateTransform tA, TranslateTransform tB, string text)
        {
            TextBlock active = _lyricShowingA ? a : b;
            TextBlock idle = _lyricShowingA ? b : a;

            active.BeginAnimation(OpacityProperty, null);
            idle.BeginAnimation(OpacityProperty, null);

            active.Text = text;
            active.Opacity = string.IsNullOrEmpty(text) ? 0 : 1;
            idle.Text = string.Empty;
            idle.Opacity = 0;
            (active == a ? tA : tB).Y = 0;
            (idle == a ? tA : tB).Y = 0;
        }

        private static DoubleAnimation Fade(double from, double to, double durationMs, IEasingFunction ease)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            };
        }

        #endregion

        #region SMTC 播放控制

        /// <summary>播放/暂停：先乐观切换图标，SMTC 回报后用真实状态校正。</summary>
        private async void OnTogglePlay(object sender, RoutedEventArgs e)
        {
            bool willPlay = !_media.IsPlaying;
            PlayButton.Content = willPlay ? "⏸" : "▶";
            _playGlyphLockUntil = DateTime.UtcNow.AddSeconds(1.5);

            await _media.TogglePlayPauseAsync();

            // 命令返回后立刻按最新状态收敛一次；仍与预期不符则等心跳兜底
            string glyph = _media.IsPlaying ? "⏸" : "▶";
            PlayButton.Content = glyph;
            _playGlyphLockUntil = DateTime.UtcNow.AddMilliseconds(400);
        }

        private void OnSkipPrevious(object sender, RoutedEventArgs e) => _ = _media.SkipPreviousAsync();

        private void OnSkipNext(object sender, RoutedEventArgs e) => _ = _media.SkipNextAsync();

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
                // 左键点击胶囊本体：展开/收起详情（按钮点击由 IsInteractive 过滤，不会走到这里）
                if (_mediaMode)
                {
                    SetDetail(!_detail);
                }

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
            int count = _feed?.Notifications.Count ?? 0;

            IdleTime.Text = DateTime.Now.ToString("HH:mm");
            IdleHint.Text = count > 0 ? string.Format("{0} 条新通知", count) : "待机";
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            StopSpring();
            _clockTimer.Stop();
            _lyricTimer.Stop();
            _topmostTimer.Stop();

            _service.Changed -= OnConfigChanged;
            _media.StateChanged -= OnMediaStateChanged;
            _lyrics.ManualImported -= OnLyricsManualImported;
            _lyricLookupCts?.Cancel();

            _feed.Dispose();
            _bridge?.Dispose();
            _glass.Dispose();

            base.OnClosed(e);
        }
    }
}
