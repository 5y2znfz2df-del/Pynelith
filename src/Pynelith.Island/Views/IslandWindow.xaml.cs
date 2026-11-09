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
using System.Windows.Media.Imaging;
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
        private const double SpringZeta = 0.82;    // 阻尼比：<1 带一点回弹，接近系统动效
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

        private IntPtr _hwnd;
        private bool _dockGuardActive;

        private string _trackFingerprint = string.Empty;
        private LrcDocument? _currentLrc;
        private int _lyricIndex = -2; // -2 表示尚未同步过
        private CancellationTokenSource? _lyricLookupCts;
        private bool _lyricShowingA;

        private IslandConfig Config => _service.Config;

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
                CaptureMargin = 90,
                LightFollowsCursor = true,
                FramesPerSecond = 60,
            });

            // ---- 通知仅用于空闲态计数（接管系统通知时关闭模拟通知） ----
            _feed = new MockFeed(Config, notificationsEnabled: !Config.TakeoverNotifications);
            SetupBridge();

            _clockTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _clockTimer.Tick += (_, _) => UpdateTexts();

            _lyricTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _lyricTimer.Tick += (_, _) => SyncLyricPosition();

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

        /// <summary>窗口固定为媒体态尺寸；胶囊在窗口内顶部居中，空闲态也天然居中。</summary>
        private void ApplyWindowSize()
        {
            double scale = Config.GlobalScale;
            Width = Math.Max(120, Config.ExpandedWidth * scale);
            Height = Math.Max(56, Config.ExpandedHeight * scale);

            ContentScale.ScaleX = scale;
            ContentScale.ScaleY = scale;

            LyricHost.Visibility = Config.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 弹簧形变（加固版）

        private (double IdleW, double IdleH, double MediaW, double MediaH) Metrics()
        {
            double scale = Config.GlobalScale;
            return (
                Config.CollapsedWidth * scale,
                Config.CollapsedHeight * scale,
                Config.ExpandedWidth * scale,
                Config.ExpandedHeight * scale);
        }

        /// <summary>把媒体态进度 p 应用到胶囊尺寸、圆角裁剪、内容交叉淡入与玻璃参数。</summary>
        private void ApplyMorph(double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p))
            {
                p = _target;
                _v = 0;
            }

            double pc = Math.Clamp(p, 0, 1);
            var m = Metrics();

            double width = m.IdleW + (m.MediaW - m.IdleW) * pc;
            double height = m.IdleH + (m.MediaH - m.IdleH) * pc;

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
            double idleAlpha = Math.Clamp(1 - p * 1.9, 0, 1);
            double mediaAlpha = Math.Clamp((p - 0.30) * 1.9, 0, 1);

            IdleView.Opacity = idleAlpha;
            IdleView.IsHitTestVisible = idleAlpha > 0.5;
            IdleScale.ScaleX = 1 + 0.10 * pc;
            IdleScale.ScaleY = 1 + 0.10 * pc;

            MediaView.Opacity = mediaAlpha;
            MediaView.IsHitTestVisible = mediaAlpha > 0.5;
            double es = 0.94 + 0.06 * Math.Clamp(p, 0, 1.08);
            MediaScale.ScaleX = es;
            MediaScale.ScaleY = es;

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
                StopSpring();
                _p = _target;
                _v = 0;
                ApplyMorph(_p);
                return;
            }

            _lastFrameTicks = Stopwatch.GetTimestamp();
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

        /// <summary>空闲/媒体形态切换（由媒体会话状态自动驱动，无手动交互）。</summary>
        private void SetMediaMode(bool media)
        {
            if (_mediaMode == media)
            {
                return;
            }

            _mediaMode = media;
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

            PlayButton.Content = _media.IsPlaying ? "⏸" : "▶";
            CoverImage.Source = _media.Artwork;
            CoverFallback.Visibility = _media.Artwork == null ? Visibility.Visible : Visibility.Collapsed;

            MediaTitle.Text = string.IsNullOrWhiteSpace(_media.Title) ? "未知曲目" : _media.Title;
            MediaArtist.Text = _media.Artist;

            bool media = _media.HasSession && !string.IsNullOrWhiteSpace(_media.Title);
            SetMediaMode(media);
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

        /// <summary>按播放位置同步当前歌词行（300ms 心跳，带 250ms 提前量）。</summary>
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

            int index = doc.LineAt(_media.Position + TimeSpan.FromMilliseconds(250));
            if (index == _lyricIndex)
            {
                return;
            }

            _lyricIndex = index;
            string text = index >= 0 ? doc.Lines[index].Text : string.Empty;
            SwapLyric(text);
        }

        /// <summary>歌词行交叉淡入切换：新行自下方滑入，旧行向上淡出（纯合成层动画，稳定 60fps）。</summary>
        private void SwapLyric(string text)
        {
            TextBlock incoming = _lyricShowingA ? LyricB : LyricA;
            TextBlock outgoing = _lyricShowingA ? LyricA : LyricB;
            var inTrans = (TranslateTransform)incoming.RenderTransform;
            var outTrans = (TranslateTransform)outgoing.RenderTransform;
            _lyricShowingA = !_lyricShowingA;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            const double durationMs = 260;

            if (string.IsNullOrWhiteSpace(text))
            {
                outgoing.BeginAnimation(OpacityProperty, Fade(outgoing.Opacity, 0, durationMs, ease));
                outTrans.BeginAnimation(TranslateTransform.YProperty, Fade(outTrans.Y, -12, durationMs, ease));
                incoming.Opacity = 0;
                return;
            }

            incoming.Text = text;
            inTrans.Y = 12;
            incoming.BeginAnimation(OpacityProperty, Fade(0, 1, durationMs, ease));
            inTrans.BeginAnimation(TranslateTransform.YProperty, Fade(12, 0, durationMs, ease));

            outgoing.BeginAnimation(OpacityProperty, Fade(outgoing.Opacity, 0, durationMs, ease));
            outTrans.BeginAnimation(TranslateTransform.YProperty, Fade(outTrans.Y, -12, durationMs, ease));
        }

        /// <summary>无动画直接设置当前可见歌词文本（清空/占位文案用）。</summary>
        private void SetLyricImmediate(string text)
        {
            TextBlock active = _lyricShowingA ? LyricA : LyricB;
            TextBlock idle = _lyricShowingA ? LyricB : LyricA;

            active.BeginAnimation(OpacityProperty, null);
            idle.BeginAnimation(OpacityProperty, null);

            active.Text = text;
            active.Opacity = string.IsNullOrEmpty(text) ? 0 : 1;
            idle.Opacity = 0;
            ((TranslateTransform)active.RenderTransform).Y = 0;
            ((TranslateTransform)idle.RenderTransform).Y = 0;
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

        private void OnTogglePlay(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

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
                // 媒体态下点击岛本体 = 播放/暂停（SMTC）；点击完成后钉回停靠位
                if (_mediaMode)
                {
                    _ = _media.TogglePlayPauseAsync();
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
