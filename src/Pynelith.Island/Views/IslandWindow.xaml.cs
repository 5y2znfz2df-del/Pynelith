using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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
using Microsoft.Win32;
using Pynelith.Glass.Effects;
using Pynelith.Island.Configuration;
using Pynelith.Island.Models;
using Pynelith.Island.Services;
using Pynelith.Island.Services.Lyrics;
using Pynelith.Island.Services.Media;
using Pynelith.Island.Services.Siri;

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

        private static readonly CultureInfo ZhCulture = CultureInfo.GetCultureInfo("zh-CN");

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
        private readonly MockFeed _feed;
        private readonly SystemMetricsService _metrics;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _lyricTimer;
        private readonly DispatcherTimer _topmostTimer;
        private readonly DispatcherTimer _springTimer;
        private readonly RectangleGeometry _capsuleClip = new RectangleGeometry();
        private readonly RectangleGeometry _highlightGeometry = new RectangleGeometry();
        private readonly RectangleGeometry _topArcGeometry = new RectangleGeometry();

        // 玻璃层按主题动态安装/卸载：纯黑、磨砂主题下不运行抓屏，省下全部采样开销
        private LiquidGlassSurface? _glass;
        private string _appliedTheme = string.Empty;
        private bool _appliedCompatibility;
        private bool _glassCompatMode; // 当前玻璃层是否为可录制的兼容模式

        // 空闲态内容实测宽度（时间 + 待机文字 + 监控），确保胶囊始终包得住内容
        private double _idleMinWidth;

        // 弹簧状态：p 为媒体态进度 0..1（允许过冲）
        private double _p;
        private double _v;
        private double _target;
        private bool _springRunning;
        private long _lastFrameTicks;

        private bool _mediaMode;
        private bool _dragging;
        private bool _pressActive;
        private bool _metricsRunning;
        private Point _pressPoint;
        private double _lastGlassWidth = -1;
        private double _lastGlassHeight = -1;
        private double _lastGlassRadius = -1;
        private double _lastGlassSplit = -1;
        private bool _glassDirty; // 配置里的玻璃参数变了，下一帧强制推给渲染模块
        private int _glassFrameSkip;
        private long _springStartTicks;

        // 详情展开弹簧（与主弹簧共用参数）：_d 为展开度 0..1
        private bool _detail;
        private double _d;
        private double _dv;

        // 消息展示：_n 为消息面板展开度 0..1，独立的弹簧通道
        private readonly Queue<NotificationItem> _messageQueue = new Queue<NotificationItem>();
        private readonly DispatcherTimer _messageHoldTimer;
        private bool _messageTargetOn;
        private bool _messageShowing;
        private double _n;
        private double _nv;
        private double _notifWidth = 300;
        private double _notifHeight = 96;

        // 欢迎动画期间的胶囊撑开：_i 为展开度 0..1，独立的弹簧通道。
        // 用户可能把胶囊收得很小（如 58×22），放不下 hello，因此动画期间临时撑开。
        private bool _introSizeTarget;
        private double _i;
        private double _iv;

        // Siri 透镜面板：_s 为展开度 0..1，独立的弹簧通道（与详情 / 消息互斥）。
        private bool _siriTarget;
        private double _s;
        private double _sv;
        private bool _siriCollapsing;

        // Siri 组件（可插拔、可单独禁用）
        private readonly SiriConfig _siriConfig;
        private readonly SiriService? _siri;
        private LensGlowSurface? _siriGlass;
        private DispatcherTimer? _siriGlowTimer;
        private DateTime _siriGlowPhase = DateTime.MinValue;
        private double _siriGlowIntensity;
        private double _siriGlowCollapse;

        // 当前展示的通知（用于消息面板收尾时清理）
        private NotificationItem? _currentNotification;

        // 通知历史（按应用分组，持久化到磁盘）
        private readonly NotificationHistoryStore _history = new NotificationHistoryStore();

        // 系统深浅色监听（跟随系统自动换配色）
        private SystemThemeService? _systemTheme;
        private bool _followSystemThemeApplied = true;

        // 入场动画是否正在播放（防止解锁与启动重复触发）
        private bool _introRunning;

        // 欢迎动画期间隐藏胶囊内的其它内容
        private bool _introActive;

        // 点击播放后短暂锁定图标，避免 SMTC 状态回报滞后把图标刷回旧值
        private DateTime _playGlyphLockUntil = DateTime.MinValue;

        // 展开面板：进度拖拽 / 音量拖拽状态
        private bool _seeking;
        private bool _updatingSeek;
        private bool _volumeDragging;
        private bool _updatingVolume;
        private int _detailTick;
        private int _volumeTick;

        // Apple Music 风格的强调色与次级白色
        private static readonly Brush AccentPink =
            new SolidColorBrush(Color.FromRgb(0xFF, 0x37, 0x5F));
        private static readonly Brush DimWhite =
            new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));

        private IntPtr _hwnd;
        private bool _dockGuardActive;

        private string _trackFingerprint = string.Empty;
        private LrcDocument? _currentLrc;
        private int _lyricIndex = -2; // -2 表示尚未同步过
        private TimeSpan _lastLyricPosition = TimeSpan.MinValue;
        private CancellationTokenSource? _lyricLookupCts;
        private bool _lyricShowingA;
        private string _currentLyricText = string.Empty;
        private int _lastAppliedLyricOffset;
        private int _lyricSourceMask = -1;
        private string _appliedProgressPresetId = string.Empty;

        // 逐字（卡拉 OK）高亮：每个歌词 TextBlock 一份（A/B 双缓冲各一份）
        private readonly Dictionary<TextBlock, KaraokeLyricText> _karaoke = new Dictionary<TextBlock, KaraokeLyricText>();

        private IslandConfig Config => _service.Config;

        /// <summary>
        /// 生效动画时长：遵循系统「辅助功能 → 显示动画」设置。
        /// 用户关闭系统动画后，形变与消息面板直接落位，不再做逐帧弹簧。
        /// </summary>
        private int EffectiveAnimationMs
            => SystemParameters.ClientAreaAnimation ? Config.AnimationDurationMs : 0;

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
            HighlightPath.Data = _highlightGeometry;
            TopArcPath.Data = _topArcGeometry;

            // 逐字（卡拉 OK）高亮：给四个歌词 TextBlock 各建一份 Inline 结构。
            // 已唱部分用强调色，未唱部分用弱化色 —— 与液态玻璃的强调色一致。
            foreach (var block in new[] { CompactLyricA, CompactLyricB, DetailLyricA, DetailLyricB })
            {
                _karaoke[block] = new KaraokeLyricText(
                    block,
                    sungColor: Color.FromRgb(0xFF, 0x37, 0x5F),
                    unsungColor: Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF));
            }

            // ---- 主题：液态玻璃才安装玻璃层；纯黑/磨砂用静态底色，零抓屏开销 ----
            ApplyTheme();

            // ---- 系统深浅色：跟随系统自动换配色 ----
            if (Config.FollowSystemTheme)
            {
                _systemTheme = new SystemThemeService();
                _systemTheme.ThemeChanged += OnSystemThemeChanged;
            }

            // ---- 系统监控：CPU / 内存采样（按配置决定是否启动） ----
            _metrics = new SystemMetricsService();
            _metrics.Updated += OnMetricsUpdated;

            // 模拟通知完全关闭：岛内只展示真实系统通知，避免演示数据混入消息流
            _feed = new MockFeed(Config, notificationsEnabled: false);
            SetupBridge();

            // ---- Siri 组件：可插拔、可单独禁用。禁用时不装玻璃、不开麦 ----
            _siriConfig = SiriConfig.Load();
            if (_siriConfig.Enabled)
            {
                _siri = new SiriService(_siriConfig);
            }
            InitSiri();

            _messageHoldTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(5000),
            };
            _messageHoldTimer.Tick += (_, _) => Guarded(OnMessageHoldElapsed);

            // 弹簧驱动改用定时器：CompositionTarget.Rendering 只在有画面需要合成时触发，
            // 纯黑 / 磨砂且内容全关时窗口完全静止，Rendering 不会来，动画会永远停在第一帧。
            _springTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(8),
            };
            _springTimer.Tick += OnSpringTimerTick;

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
                // 20ms 采样：切行延迟 ≤20ms，配合播放位置的 80ms 校正容差，整体误差稳定在 100ms 内。
                // 优先级用 Normal：Render 优先级会被逐帧动画长期抢占，导致歌词长时间不更新。
                Interval = TimeSpan.FromMilliseconds(20),
            };
            _lyricTimer.Tick += (_, _) => Guarded(() =>
            {
                SyncLyricPosition();
                SyncPlaybackGlyph();
                UpdateDetailProgress();
            });

            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            _topmostTimer.Tick += (_, _) => ReassertTopmost();

            // 进度条：拖拽期间冻结跟随，松手后一次跳转
            SeekSlider.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
            SeekSlider.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (_seeking)
                {
                    _seeking = false;
                    CommitSeek();
                }
            };
            SeekSlider.AddHandler(Thumb.DragStartedEvent,
                new DragStartedEventHandler((_, _) => _seeking = true));
            SeekSlider.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler((_, _) =>
                {
                    _seeking = false;
                    CommitSeek();
                }));
            SeekSlider.ValueChanged += OnSeekValueChanged;

            VolumeSlider.ValueChanged += OnVolumeValueChanged;
            VolumeSlider.AddHandler(Thumb.DragStartedEvent,
                new DragStartedEventHandler((_, _) => _volumeDragging = true));
            VolumeSlider.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler((_, _) => _volumeDragging = false));

            // 键盘无障碍：面板展开后可用空格 / 方向键 / Esc 操作，无需精确点击小控件
            PreviewKeyDown += OnIslandKeyDown;

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

            // 静止时玻璃低频采样，形变时再提到 24fps（见 EnsureSpring）
            _glass?.SetFrameRate(10);

            _clockTimer.Start();
            _lyricTimer.Start();
            _topmostTimer.Start();

            _metricsRunning = Config.ShowSystemMetrics;
            if (_metricsRunning)
            {
                _metrics.Start();
            }

            // 启动即同步一次媒体状态（会话服务可能已先就绪）
            OnMediaStateChanged(this, EventArgs.Empty);

            // 开机进入桌面：胶囊从顶端滑入
            PlayIntro();

            // Siri 组件：加载离线唤醒模型（首次会下载约 42MB），延后到首屏之后
            StartSiriAsync();

            // 锁屏解锁同样播放一次（Win+L 之后回到桌面）
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        /// <summary>锁屏 / 解锁切换：只有解锁回来时才播放入场。</summary>
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                PlayIntro();
            }
        }

        #region 入场动画

        /// <summary>
        /// 欢迎动画素材：彩虹手写 "hello"。
        ///
        /// 精灵图按纵向排列，每帧占固定行高；播放时用 <see cref="CroppedBitmap"/>
        /// 逐帧裁出并切给 Image，因此不需要任何外部动画库。
        /// </summary>
        private const int IntroHelloFrameHeight = 96;

        /// <summary>每帧显示时长（毫秒）。45 帧 × 50ms ≈ 2.25s 写完，比原片略快一点，更利落。</summary>
        private const int IntroHelloFrameMs = 50;

        /// <summary>hello 画完后的停留时间（毫秒），之后再淡出。</summary>
        private const int IntroHelloHoldMs = 420;

        /// <summary>欢迎动画帧序列（懒加载，构造时不阻塞首屏）。</summary>
        private static readonly Lazy<IReadOnlyList<BitmapSource>> IntroHelloFrames =
            new Lazy<IReadOnlyList<BitmapSource>>(LoadIntroHelloFrames);

        /// <summary>欢迎动画播放定时器。</summary>
        private DispatcherTimer? _introFrameTimer;

        /// <summary>欢迎动画当前帧序号。</summary>
        private int _introFrameIndex;

        /// <summary>把精灵图按固定行高切成独立帧。</summary>
        private static IReadOnlyList<BitmapSource> LoadIntroHelloFrames()
        {
            var frames = new List<BitmapSource>();

            try
            {
                var uri = new Uri("pack://application:,,,/Pynelith.Island;component/Assets/Intro/hello.png");
                var decoder = new PngBitmapDecoder(
                    uri,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                BitmapSource sheet = decoder.Frames[0];
                int frameHeight = Math.Min(IntroHelloFrameHeight, sheet.PixelHeight);

                for (int y = 0; y + frameHeight <= sheet.PixelHeight; y += frameHeight)
                {
                    var crop = new CroppedBitmap(
                        sheet,
                        new Int32Rect(0, y, sheet.PixelWidth, frameHeight));

                    frames.Add(crop);
                }
            }
            catch (Exception)
            {
                // 素材缺失时退化为"无动画"，不影响岛的其它功能。
            }

            return frames;
        }

        /// <summary>
        /// 入场：胶囊从屏幕顶端之上滑入，先快后慢。
        ///
        /// 只改视觉位移（<see cref="IntroTranslate"/>），不动窗口位置，
        /// 因此不会与停靠守护（拦截 WM_WINDOWPOSCHANGING）冲突。
        /// 系统关闭「显示动画」时直接落位。
        /// </summary>
        private void PlayIntro()
        {
            if (_introRunning || !IsVisible)
            {
                return;
            }

            double startOffset = -(Height + 24);

            if (!SystemParameters.ClientAreaAnimation)
            {
                IntroTranslate.Y = 0;
                return;
            }

            _introRunning = true;
            _introActive = true;

            // 撑开胶囊：用户可能把胶囊配得很小，不撑开 hello 会被裁掉
            _introSizeTarget = true;
            EnsureSpring();

            ApplyMorph(_p);

            var drop = new DoubleAnimation
            {
                From = startOffset,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(460),
                // 先快后慢：EaseOut 起步速度最高，收尾最慢
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };

            drop.Completed += (_, _) =>
            {
                IntroTranslate.Y = 0;
                _introRunning = false;
                PlayIntroAnimation();
            };

            IntroTranslate.BeginAnimation(TranslateTransform.YProperty, drop);
        }

        /// <summary>
        /// 欢迎动画：彩虹手写 "hello" 逐帧书写，停留片刻后淡出。
        /// 只有这段动画消失，胶囊照常显示。
        /// </summary>
        private void PlayIntroAnimation()
        {
            IReadOnlyList<BitmapSource> frames = IntroHelloFrames.Value;

            if (!SystemParameters.ClientAreaAnimation || frames.Count == 0)
            {
                RestoreIntroContent();
                return;
            }

            _introFrameIndex = 0;
            IntroHelloImage.Source = frames[0];
            IntroHello.Visibility = Visibility.Visible;
            IntroHello.Opacity = 1;

            _introFrameTimer?.Stop();
            _introFrameTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(IntroHelloFrameMs),
            };
            _introFrameTimer.Tick += OnIntroFrameTick;
            _introFrameTimer.Start();
        }

        /// <summary>逐帧推进；播完后进入停留 / 淡出阶段。</summary>
        private void OnIntroFrameTick(object? sender, EventArgs e)
        {
            IReadOnlyList<BitmapSource> frames = IntroHelloFrames.Value;
            _introFrameIndex++;

            if (_introFrameIndex >= frames.Count)
            {
                _introFrameTimer?.Stop();
                HoldThenFadeHello();
                return;
            }

            IntroHelloImage.Source = frames[_introFrameIndex];
        }

        /// <summary>画完后停留一会儿再淡出，避免还没看清就消失。</summary>
        private void HoldThenFadeHello()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(IntroHelloHoldMs),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var fadeOut = Fade(1, 0, 320, new CubicEase { EasingMode = EasingMode.EaseIn });
                fadeOut.Completed += (_, _) => ResetHello();
                IntroHello.BeginAnimation(OpacityProperty, fadeOut);
            };

            timer.Start();
        }

        /// <summary>收尾：折叠并清掉动画，恢复胶囊内的常规内容。</summary>
        private void ResetHello()
        {
            _introFrameTimer?.Stop();
            _introFrameTimer = null;

            IntroHello.Visibility = Visibility.Collapsed;
            IntroHello.BeginAnimation(OpacityProperty, null);
            IntroHello.Opacity = 0;
            IntroHelloImage.Source = null;

            RestoreIntroContent();
        }

        /// <summary>结束欢迎态，让时间 / 歌词 / 播放控件按当前形态重新淡入。</summary>
        private void RestoreIntroContent()
        {
            // 收回胶囊到原尺寸
            if (_introSizeTarget)
            {
                _introSizeTarget = false;
                EnsureSpring();
            }

            if (!_introActive)
            {
                return;
            }

            _introActive = false;
            ApplyMorph(_p);
        }

        #endregion

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
                AttachSiriHotkey(src);
            }
        }

        /// <summary>
        /// 逐屏 DPI 变化：岛被拖到缩放不同的屏上时，WPF 会发 WM_DPICHANGED。
        /// 此时必须按新屏的 DPI 重新计算停靠位置，否则岛会停在错误的像素坐标上。
        /// </summary>
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);

            if (!_dragging)
            {
                Reposition();
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
                        // 逐屏 DPI：目标屏可能是缩放不同的那块，
                        // 用它自己的 DPI 把 DIP 坐标换算成物理像素。
                        var monitor = TargetWorkArea();
                        double dpi = monitor.DpiScale;
                        Rect work = monitor.WorkArea;

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

            if (!string.Equals(_appliedTheme, Config.Theme, StringComparison.Ordinal)
                || _appliedCompatibility != Config.ScreenshotCompatible)
            {
                ApplyTheme();
            }
            else if (_glass != null)
            {
                // 染色浓度 / 圆角这类参数不改变胶囊尺寸，必须显式标记一次重推，
                // 否则 ApplyGlassShape 会以"尺寸没变"为由直接返回。
                _glassDirty = true;
            }

            ApplyMetricsTimer();

            ApplyWindowSize();
            ApplyMorph(_p);
            UpdateTexts();
            Reposition();
            // 只有歌词开关/歌词源变化才重新匹配；单纯调偏移不应触发重新查询
            int sourceMask = (Config.LyricsEnabled ? 1 : 0)
                | (Config.LyricsSourceNetease ? 2 : 0)
                | (Config.LyricsSourceQQ ? 4 : 0)
                | (Config.LyricsSourceKugou ? 8 : 0);

            if (sourceMask != _lyricSourceMask)
            {
                _lyricSourceMask = sourceMask;

                if (Config.LyricsEnabled && _mediaMode && !string.IsNullOrWhiteSpace(_media.Title))
                {
                    BeginLyricLookup(_media.Title, _media.Artist);
                }
            }

            // 偏移变化要立即重算当前行（设置页调整后无需重启）
            if (_lastAppliedLyricOffset != Config.LyricOffsetMs)
            {
                _lastAppliedLyricOffset = Config.LyricOffsetMs;
                _lyricIndex = -2;
                SyncLyricPosition();
            }

            // 进度对齐预设变化：立即套用到播放位置推算上
            if (_appliedProgressPresetId != Config.ProgressOffsetPresetId)
            {
                _appliedProgressPresetId = Config.ProgressOffsetPresetId;
                _media.ApplyOffsetPreset(Config.ProgressOffsetPresetId);
            }

            // 跟随系统深浅色开关变化：启停监听并立即重刷配色
            if (_followSystemThemeApplied != Config.FollowSystemTheme)
            {
                _followSystemThemeApplied = Config.FollowSystemTheme;
                ApplyFollowSystemTheme();
            }
        }

        /// <summary>按配置启停系统深浅色监听。</summary>
        private void ApplyFollowSystemTheme()
        {
            if (Config.FollowSystemTheme)
            {
                if (_systemTheme == null)
                {
                    _systemTheme = new SystemThemeService();
                    _systemTheme.ThemeChanged += OnSystemThemeChanged;
                }
            }
            else
            {
                if (_systemTheme != null)
                {
                    _systemTheme.ThemeChanged -= OnSystemThemeChanged;
                    _systemTheme.Dispose();
                    _systemTheme = null;
                }
            }

            ApplyTheme();
        }

        #region 主题与系统监控

        /// <summary>
        /// 应用界面主题：液态玻璃安装抓屏玻璃层；纯黑 / 磨砂改用静态底色并卸载玻璃层。
        /// 三套主题共用同一套排版与动画，切换只改变背景与开销，不影响胶囊尺寸。
        /// </summary>
        private void ApplyTheme()
        {
            _appliedTheme = Config.Theme;
            _appliedCompatibility = Config.ScreenshotCompatible;

            // 液态玻璃：两种模式都做实时抓屏折射。
            // 常规模式把窗口排除出捕获（更高清，但截图/录屏里岛会消失）；
            // 兼容模式不排除捕获（截图/录屏能看到岛），采样时遮掉自身区域避免自反馈，
            // 折射带、色散、染色与边缘高光保持一致。
            bool liquidGlass = string.Equals(_appliedTheme, ThemeStyles.LiquidGlass, StringComparison.Ordinal);
            bool recordableGlass = liquidGlass && _appliedCompatibility;

            if (liquidGlass)
            {
                ThemeBackdrop.Background = Brushes.Transparent;
                ThemeBackdrop.BorderThickness = new Thickness(0);

                // 从实时玻璃切到兼容玻璃（或反向）时必须重建玻璃层。
                if (_glass == null || _glassCompatMode != recordableGlass)
                {
                    _glass?.Dispose();
                    _glass = InstallGlass(recordableGlass);
                    _glassCompatMode = recordableGlass;
                }

                // Siri 透镜的显示亲和力作用在同一个窗口上，也必须跟着一起换
                RefreshSiriGlass();
            }
            else
            {
                if (_glass != null)
                {
                    _glass.Dispose();
                    _glass = null;
                    _glassCompatMode = false;
                }

                if (string.Equals(_appliedTheme, ThemeStyles.PureBlack, StringComparison.Ordinal))
                {
                    // 纯黑：不透明，零抓屏开销；用很轻的纵向渐变让弧面在纯黑里也有体积感
                    ThemeBackdrop.Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(0, 1),
                        GradientStops =
                        {
                            new GradientStop(Color.FromRgb(0x15, 0x18, 0x1E), 0.0),
                            new GradientStop(Color.FromRgb(0x0A, 0x0C, 0x10), 0.45),
                            new GradientStop(Color.FromRgb(0x04, 0x05, 0x07), 1.0),
                        },
                    };
                    // 描边不是均匀白圈：上缘亮、腰身消失、底缘只留一线环境反光
                    ThemeBackdrop.BorderBrush = RimBrush(0x1A, 0x05, 0x0D);
                }
                else
                {
                    // 磨砂：深黑灰半透明 + 柔和纵向渐变
                    ThemeBackdrop.Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(0, 1),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0xDC, 0x23, 0x26, 0x2C), 0.0),
                            new GradientStop(Color.FromArgb(0xE8, 0x12, 0x14, 0x18), 0.5),
                            new GradientStop(Color.FromArgb(0xF0, 0x07, 0x08, 0x0A), 1.0),
                        },
                    };
                    ThemeBackdrop.BorderBrush = RimBrush(0x1E, 0x06, 0x10);
                }

                ThemeBackdrop.BorderThickness = new Thickness(0.75);
            }

            // 系统浅色模式：纯黑 / 磨砂换成浅色配色，图标与文字反相，
            // 避免在浅色桌面上一块黑砖头。
            ApplySystemLightPalette(liquidGlass);

            // 玻璃层重建后强制下一帧把形状参数推过去
            _lastGlassWidth = -1;
            _lastGlassHeight = -1;
            _lastGlassRadius = -1;
            _lastGlassSplit = -1;
            _glassDirty = true;
            _glassFrameSkip = 0;
            ApplyMorph(_p);
        }

        /// <summary>
        /// 跟随系统深浅色：浅色系统下把纯黑 / 磨砂换成浅色底，
        /// 并把前景文字与图标反相。
        /// 液态玻璃本身是抓屏折射，背景由桌面决定，不需要改配色。
        /// </summary>
        private void ApplySystemLightPalette(bool liquidGlass)
        {
            bool light = Config.FollowSystemTheme
                && _systemTheme?.Current == SystemThemeMode.Light;

            if (light && !liquidGlass)
            {
                if (string.Equals(_appliedTheme, ThemeStyles.PureBlack, StringComparison.Ordinal))
                {
                    // 浅色纯黑：近乎白的底 + 极淡的描边
                    ThemeBackdrop.Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(0, 1),
                        GradientStops =
                        {
                            new GradientStop(Color.FromRgb(0xFA, 0xFA, 0xFC), 0.0),
                            new GradientStop(Color.FromRgb(0xF2, 0xF3, 0xF6), 0.45),
                            new GradientStop(Color.FromRgb(0xE9, 0xEA, 0xEE), 1.0),
                        },
                    };
                    ThemeBackdrop.BorderBrush = RimBrush(0x28, 0x14, 0x1C);
                }
                else
                {
                    // 浅色磨砂：半透明白 + 柔和阴影感
                    ThemeBackdrop.Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(0, 1),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 0.0),
                            new GradientStop(Color.FromArgb(0xEA, 0xF6, 0xF7, 0xFA), 0.5),
                            new GradientStop(Color.FromArgb(0xE4, 0xEE, 0xF0, 0xF4), 1.0),
                        },
                    };
                    ThemeBackdrop.BorderBrush = RimBrush(0x30, 0x16, 0x1E);
                }
            }
            else if (!liquidGlass)
            {
                // 深色：恢复默认配色（ApplyTheme 的 else 分支已经设过，这里无需处理）
            }

            // 前景色（文字 / 图标）跟随系统深浅反相
            ApplyForegroundPalette(light);
        }

        /// <summary>按系统深浅切换文字与图标的配色。</summary>
        private void ApplyForegroundPalette(bool light)
        {
            var primary = light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF);
            var secondary = light ? Color.FromRgb(0x6B, 0x6B, 0x70) : Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF);
            var tertiary = light ? Color.FromRgb(0xA0, 0xA0, 0xA6) : Color.FromArgb(0x6B, 0xFF, 0xFF, 0xFF);
            var iconPrimary = light ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF);
            var iconSecondary = light ? Color.FromRgb(0x7A, 0x7A, 0x80) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);

            SetResource("TextPrimary", primary);
            SetResource("TextSecondary", secondary);
            SetResource("TextTertiary", tertiary);
            SetResource("IconPrimary", iconPrimary);
            SetResource("IconSecondary", iconSecondary);
        }

        /// <summary>更新窗口资源字典里的画刷（颜色变化时重建画刷）。</summary>
        private void SetResource(string key, Color color)
        {
            if (Resources[key] is SolidColorBrush existing && existing.Color == color)
            {
                return;
            }

            Resources[key] = new SolidColorBrush(color);
        }

        /// <summary>
        /// 系统深浅色变化：重刷配色。
        /// 只改背景与前景画刷，不动玻璃层（玻璃是抓屏折射，与系统深浅无关）。
        /// </summary>
        private void OnSystemThemeChanged(object? sender, EventArgs e)
        {
            if (!_hwnd.Equals(IntPtr.Zero) && !IsVisible)
            {
                return;
            }

            ApplyTheme();
        }

        /// <summary>
        /// 玻璃外框的描边：上缘是主反光，腰身完全消失，底缘只留极淡的环境反光。
        /// 用纵向渐变代替均匀白圈，避免一圈等亮的"塑料贴纸边"。
        /// </summary>
        private static Brush RimBrush(byte top, byte waist, byte bottom)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(top, 0xFF, 0xFF, 0xFF), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(waist, 0xFF, 0xFF, 0xFF), 0.22));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.44));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.78));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(bottom, 0xFF, 0xFF, 0xFF), 1.0));
            return brush;
        }

        private LiquidGlassSurface InstallGlass(bool recordable)
        {
            // 两种模式只在「是否排除出抓屏」上不同：
            // 常规模式把窗口排除，采样天然拿到干净桌面；兼容模式不排除（录屏里能看到岛），
            // 采样时用遮罩抹掉自身区域，因此折射带、色散、边缘高光的参数与观感完全一致。
            var glass = LiquidGlassSurface.Install(CapsuleHost, new LiquidGlassSurface.LiquidGlassStyle
            {
                CornerRadius = Config.CornerRadius,
                // 更窄的折射带、更低的色散：轻微折射，不做金属镜面
                BezelWidth = 18,
                Thickness = 30,
                GlassIor = 1.45,
                // 0.02 几乎看不见；0.07 才能在边缘看到真实 RGB 分离
                Dispersion = 0.07,
                TintAmount = Config.GlassBlurStrength,
                TintTop = Color.FromRgb(10, 11, 14),
                TintBottom = Color.FromRgb(27, 31, 38),
                CaptureMargin = 50,
                // 兼容模式同样做实时折射，只把窗口留在抓屏范围内，截图/录屏可见；
                // 内部合成方式与常规模式完全一致（不透明的采样玻璃），
                // 被窗口挡住的背景由采样遮罩的纵向插值重建。
                UseScreenBackdrop = true,
                ExcludeFromCapture = !recordable,
                TransparentInterior = false,
                SurfaceOpacity = 1.0,
                // 固定光源更克制；跟随鼠标的大面积游动高光会显得"油腻"
                LightFollowsCursor = false,
                // 抓屏在 UI 线程执行，是逐帧动画里最重的一步；玻璃只是背景模糊，
                // 24fps 采样肉眼无差别，抓屏面积也一并收窄，给形变动画腾出 CPU。
                FramesPerSecond = 24,
            });

            // 光源固定在正上方：镜面高光落在上缘，形成一条窄弧，
            // 而不是默认光源方位（右缘）那条横向硬光斑。
            glass.Update(effect => effect.LightAngle = -Math.PI / 2);
            return glass;
        }

        /// <summary>按配置启停监控采样：关闭后不再有任何定时读取，主线程零负担。</summary>
        private void ApplyMetricsTimer()
        {
            if (Config.ShowSystemMetrics == _metricsRunning)
            {
                return;
            }

            _metricsRunning = Config.ShowSystemMetrics;

            if (_metricsRunning)
            {
                _metrics.Start();
            }
            else
            {
                _metrics.Stop();
                IdleMetrics.Text = string.Empty;
            }

            UpdateTexts();
        }

        private void OnMetricsUpdated(object? sender, EventArgs e) => UpdateMetricsText();

        /**
         * 监控文本固定宽度（等宽字体），数值变化不会改变胶囊宽度；
         * 任一指标超过 85% 时转为警示色，便于一眼看出高负载。
         */
        private void UpdateMetricsText()
        {
            if (!Config.ShowSystemMetrics)
            {
                IdleMetrics.Text = string.Empty;
                IdleDetailMetrics.Text = string.Empty;
                return;
            }

            if (!_metrics.IsAvailable)
            {
                IdleMetrics.Text = "CPU --%  RAM --%";
                IdleDetailMetrics.Text = "CPU --% · RAM --%";
                return;
            }

            IdleMetrics.Text = string.Format(
                "CPU {0,3:0}%  RAM {1,2:0}%",
                _metrics.CpuPercent,
                _metrics.MemoryPercent);

            IdleDetailMetrics.Text = string.Format(
                "CPU {0:0}% · RAM {1:0.0}%",
                _metrics.CpuPercent,
                _metrics.MemoryPercent);

            double peak = Math.Max(_metrics.CpuPercent, _metrics.MemoryPercent);
            Brush brush = peak >= 85
                ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                : (Brush)FindResource("TextSecondary");

            IdleMetrics.Foreground = brush;
            IdleDetailMetrics.Foreground = brush;
        }

        #endregion

        #region 系统通知接管

        private SystemNotificationBridge? _bridge;
        private bool _takeoverActive;

        /// <summary>按配置创建/重建通知桥；不可用时回退模拟通知。</summary>
        private void SetupBridge()
        {
            _takeoverActive = Config.TakeoverNotifications;

            _bridge?.Dispose();
            _bridge = null;

            // 是否"接管"只决定要不要撤下系统原生 Toast；
            // 消息本身始终监听，用于自动展开岛内展示。
            _bridge = SystemNotificationBridge.TryCreate(_takeoverActive);
            if (_bridge == null)
            {
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
                bridge.Start();
            }
            // 未授权时保持安静：不再生成模拟消息，设置页里有授权引导链接。
        }

        private void OnSystemNotification(NotificationItem item)
        {
            // 不做黑白名单过滤：所有系统通知一律进入岛内展示队列
            _history.Add(item);
            EnqueueMessage(item);
        }

        #region 消息展示

        /// <summary>
        /// 收到一条消息：直接入队（不做任何黑白名单过滤）。
        /// 当前没有消息时立即展开；否则排队，等当前消息停留结束后接着展示。
        /// </summary>
        private void EnqueueMessage(NotificationItem item)
        {
            string text = NormalizeMessage(item.FullText);
            if (text.Length == 0)
            {
                return;
            }

            _messageQueue.Enqueue(item);
            while (_messageQueue.Count > 20)
            {
                _messageQueue.Dequeue();
            }

            if (!_messageShowing || !_messageTargetOn)
            {
                StartNextMessage();
            }
        }

        /// <summary>取出队列中的下一条消息，展开面板并开始停留计时。</summary>
        private void StartNextMessage()
        {
            if (_messageQueue.Count == 0)
            {
                return;
            }

            NotificationItem item = _messageQueue.Dequeue();
            _currentNotification = item;

            _messageShowing = true;
            _detail = false; // 消息结束后回到默认紧凑形态

            // 面板互斥：消息面板展开时收起 Siri 透镜
            if (_siriTarget)
            {
                CollapseSiri();
            }

            MessageApp.Text = item.AppName;
            MessageBody.Text = item.FullText;

            ApplyMessageLayout();

            ApplyWindowSize();
            if (!_dragging)
            {
                Reposition();
            }

            // 停留时长沿用「消息停留」配置，默认约 5 秒
            _messageHoldTimer.Stop();
            _messageHoldTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Clamp(Config.NotificationDurationMs, 1500, 15000));
            _messageHoldTimer.Start();

            AnimateMessage(true);
        }

        /// <summary>停留结束：队列里还有消息就继续展示，否则平滑收回。</summary>
        private void OnMessageHoldElapsed()
        {
            _messageHoldTimer.Stop();

            if (_messageQueue.Count > 0)
            {
                StartNextMessage();
                return;
            }

            AnimateMessage(false);
        }

        /// <summary>点击消息或需要强制收起时：清空排队消息并平滑收回。</summary>
        private void DismissMessages()
        {
            _messageQueue.Clear();
            _messageHoldTimer.Stop();
            AnimateMessage(false);
        }

        /// <summary>展开 / 收回消息面板：按配置动画时长走弹簧，0 时长时立即落位。</summary>
        private void AnimateMessage(bool expanded)
        {
            _messageTargetOn = expanded;

            if (EffectiveAnimationMs <= 0)
            {
                _n = expanded ? 1 : 0;
                _nv = 0;
                StopSpring();
                ApplyMorph(_p);

                if (!expanded)
                {
                    FinishMessageCollapse();
                }

                return;
            }

            EnsureSpring();
        }

        /// <summary>消息完全收回后恢复窗口尺寸并清空文本，避免残留内容占内存。</summary>
        private void FinishMessageCollapse()
        {
            _messageShowing = false;
            _detail = false;
            _d = 0;
            _dv = 0;

            MessageApp.Text = string.Empty;
            MessageBody.Text = string.Empty;
            _currentNotification = null;

            ApplyWindowSize();
            ApplyMorph(_p);

            if (!_dragging)
            {
                Reposition();
            }
        }

        /// <summary>
        /// 按文本实测消息面板尺寸：宽度取屏宽的合理上限，正文自动换行；
        /// 高度按文字行数增长，超长文本逐级缩小字号直到完整放下（不截断）。
        /// </summary>
        private void ApplyMessageLayout()
        {
            double scale = Math.Max(0.01, Config.GlobalScale);
            Rect work = SystemParameters.WorkArea;

            double padding = 16;
            double maxPreWidth = Math.Max(200, work.Width / scale - 48);
            double preWidth = Math.Min(420, maxPreWidth);
            double bodyWidth = Math.Max(140, preWidth - padding * 2);

            // 允许消息面板占据接近整屏的高度，配合字号收缩，常见长消息都能完整放下
            double maxPreHeight = Math.Max(160, Math.Min(520, work.Height / scale - 60));

            // 固定 chrome：应用名行高 + 间距 + 上下内边距
            double chrome = 18 + 4 + 24;

            double fontSize = 12.5;
            double bodyHeight;

            while (true)
            {
                MessageBody.FontSize = fontSize;
                MessageBody.Width = bodyWidth;
                MessageBody.Measure(new Size(bodyWidth, double.PositiveInfinity));
                bodyHeight = MessageBody.DesiredSize.Height;

                if (chrome + bodyHeight <= maxPreHeight || fontSize <= 8.0)
                {
                    break;
                }

                fontSize -= 0.5;
            }

            // 极端超长消息的兜底：限制可视高度，超出部分通过滚动条查看，绝不让文字溢出胶囊
            double bodyArea = Math.Max(40, maxPreHeight - chrome);
            double visibleHeight = Math.Min(bodyHeight, bodyArea);
            MessageScroll.Height = visibleHeight;

            double preHeight = Math.Clamp(chrome + visibleHeight, 64, maxPreHeight);
            NotificationLayer.Width = preWidth;
            NotificationLayer.Height = preHeight;

            _notifWidth = preWidth * scale;
            _notifHeight = preHeight * scale;
        }

        /// <summary>去掉控制字符（保留换行/制表），避免乱码影响排版。</summary>
        private static string NormalizeMessage(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(body.Length);
            foreach (char c in body)
            {
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n' || c == '\t' || !char.IsControl(c))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Trim();
        }

        #endregion

        #endregion

        /// <summary>窗口固定为最大形态尺寸（详情展开态）；胶囊按当前形态在窗口内顶部居中。</summary>
        private void ApplyWindowSize()
        {
            double scale = Config.GlobalScale;
            var m = Metrics();

            double windowWidth = Math.Max(m.DetailW, m.MediaW);
            double windowHeight = Math.Max(m.DetailH, m.IdleH);

            // Siri 透镜（正圆 + 下方文字）比常规形态更高：窗口必须让出空间，
            // 否则透镜底部与错误提示会被窗口边界裁掉。
            if (_siri != null)
            {
                windowWidth = Math.Max(windowWidth, 260 * scale);
                windowHeight = Math.Max(windowHeight, 256 * scale + 8);
            }

            // 消息面板可能比常规形态更大：窗口需要临时让出足够空间，避免文本被裁切
            if (_messageShowing)
            {
                windowWidth = Math.Max(windowWidth, _notifWidth);
                windowHeight = Math.Max(windowHeight, _notifHeight);
            }

            Width = windowWidth;
            Height = windowHeight;

            ContentScale.ScaleX = scale;
            ContentScale.ScaleY = scale;

            // 欢迎动画层在 ContentHost 之外，需自己跟随全局缩放
            IntroHelloScale.ScaleX = scale;
            IntroHelloScale.ScaleY = scale;

            // 紧凑态文字随胶囊高度收放：36 高 ≈ 12px，更矮时自动变小，避免字被上下裁切
            double compactFont = Math.Clamp(Config.CollapsedHeight * 0.34, 7, 12);
            CompactLyricA.FontSize = compactFont;
            CompactLyricB.FontSize = compactFont;

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

            // 空闲态宽度取「配置值」与「内容实测宽度」的较大者：
            // 用户关掉监控/时间后胶囊会自然收窄，新增内容时又不会被裁切。
            double idleWidth = Math.Max(Config.CollapsedWidth, _idleMinWidth) * scale;

            // 展开面板宽度上限 = 屏幕宽度 × 0.42（1080p ≈ 800px，2K ≈ 1070px），
            // 同时不小于内容需要的 320。
            Rect work = SystemParameters.WorkArea;
            double detailWidth = Math.Min(
                Math.Max(320 * scale, mediaWidth),
                Math.Max(240, work.Width * 0.42));

            return (
                idleWidth,
                idleHeight,
                mediaWidth,
                idleHeight,
                // 展开态是仿 iOS 灵动岛的播放面板：320×140（高度落在规范的 80~140 内）
                detailWidth,
                Math.Max(140 * scale, idleHeight));
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
            double d = Math.Clamp(_d, 0, 1);

            // 展开在两种形态下都有效：待机展开（大时钟）与媒体展开（曲目详情）
            double mediaDetail = d * pc;
            double idleDetail = d * (1 - pc);
            double scale = Math.Max(0.01, Config.GlobalScale);
            var m = Metrics();

            double idleDetailWidth = Math.Max(m.IdleW, 224 * scale);
            double idleDetailHeight = Math.Max(60 * scale, m.IdleH);
            double width = m.IdleW
                + (m.MediaW - m.IdleW) * pc
                + (m.DetailW - m.MediaW) * mediaDetail
                + (idleDetailWidth - m.IdleW) * idleDetail;
            double height = m.IdleH
                + (idleDetailHeight - m.IdleH) * idleDetail
                + (m.DetailH - m.IdleH) * mediaDetail;

            // 消息层：独立弹簧把胶囊从当前形态平滑拉到消息面板尺寸（再平滑收回）
            double n = Math.Clamp(_n, 0, 1);
            if (n > 0.0001)
            {
                width += (_notifWidth - width) * n;
                height += (_notifHeight - height) * n;
            }

            // 欢迎动画：独立弹簧把胶囊撑到能容纳 hello 的尺寸，播完再收回原形。
            // 用户可能把胶囊配置得很小，不撑开就会把 hello 裁掉。
            double intro = Math.Clamp(_i, 0, 1);
            if (intro > 0.0001)
            {
                double introWidth = 86 * scale;
                double introHeight = 34 * scale;
                width += (introWidth - width) * intro;
                height += (introHeight - height) * intro;
            }

            // Siri 透镜：独立弹簧把胶囊拉到透镜尺寸（透镜是正圆 + 下方文字）
            double siri = Math.Clamp(_s, 0, 1);
            double siriAlpha = Math.Clamp(siri * 1.2, 0, 1);
            if (siri > 0.0001)
            {
                double siriWidth = 260 * scale;
                double siriHeight = 256 * scale;
                width += (siriWidth - width) * siri;
                height += (siriHeight - height) * siri;
            }

            CapsuleHost.Width = width;
            CapsuleHost.Height = height;

            ContentHost.Width = width / scale;
            ContentHost.Height = height / scale;

            // 小尺寸时按高度一半裁成标准胶囊；展开成播放面板后收敛到配置圆角，
            // 避免 224 高的面板变成两端半圆的"巨型药丸"，更接近 macOS 面板。
            double pillRadius = Math.Min(width, height) * 0.5;
            double radius = Math.Min(Config.CornerRadius * scale, pillRadius);
            _capsuleClip.Rect = new Rect(0, 0, width, height);
            _capsuleClip.RadiusX = radius;
            _capsuleClip.RadiusY = radius;

            // 纯黑 / 磨砂用真正的圆角 Border 绘制底色，描边会沿弧线走，
            // 比"矩形填充 + 父级裁剪"边缘更干净、更接近灵动岛的胶囊感。
            ThemeBackdrop.CornerRadius = new CornerRadius(radius);

            // 高光：沿胶囊轮廓画柔和弧线，而不是整圈白描边。
            // 收缩 0.5px 让描边完整落在裁剪区内，避免被裁掉一半。
            var highlightRect = new Rect(0.5, 0.5,
                Math.Max(0, width - 1), Math.Max(0, height - 1));
            _highlightGeometry.Rect = highlightRect;
            _highlightGeometry.RadiusX = Math.Max(0, radius - 0.5);
            _highlightGeometry.RadiusY = Math.Max(0, radius - 0.5);
            _topArcGeometry.Rect = highlightRect;
            _topArcGeometry.RadiusX = Math.Max(0, radius - 0.5);
            _topArcGeometry.RadiusY = Math.Max(0, radius - 0.5);

            // 未展开时更克制，展开后略微增强；
            // 媒体展开面板的下半区要完全透明，这两层覆盖（顶部柔光 / 底部暗角 / 轮廓弧线）
            // 也必须一起淡出，否则透明区会残留一层灰雾和一圈亮边。
            // Siri 透镜接管时还要整块淡出，否则胶囊本体会露出一个方形底框。
            double shellFade = (1.0 - Math.Clamp(mediaDetail, 0, 1)) * (1 - siriAlpha);
            CapsuleSheen.Opacity = (0.45 + 0.55 * Math.Clamp(_d, 0, 1)) * shellFade;
            CapsuleHighlight.Opacity = (0.5 + 0.5 * Math.Clamp(_d, 0, 1)) * shellFade;
            ThemeBackdrop.Opacity = 1 - siriAlpha;
            if (_glass != null)
            {
                _glass.Opacity = 1 - siriAlpha;
            }

            // 四层交叉淡入：待机 ⇄ 待机展开 ⇄ 媒体紧凑 ⇄ 媒体展开
            double idleAlpha = Math.Clamp(1 - pc * 1.9, 0, 1) * Math.Clamp(1 - d * 1.8, 0, 1);
            double idleDetailAlpha = Math.Clamp(1 - pc * 1.9, 0, 1) * Math.Clamp((d - 0.15) / 0.85, 0, 1);
            double compactAlpha = Math.Clamp(pc * 2.2, 0, 1) * Math.Clamp(1 - mediaDetail * 1.8, 0, 1);
            double detailAlpha = Math.Clamp((mediaDetail - 0.18) / 0.82, 0, 1);

            // 消息层接管时，原有内容整体淡出，避免文字叠在一起
            double notificationAlpha = Math.Clamp(n * 1.35, 0, 1);
            double baseAlpha = 1 - notificationAlpha;

            // 欢迎动画期间：胶囊内只显示 hello，时间/歌词/播放控件等全部隐藏
            if (_introActive)
            {
                baseAlpha = 0;
            }

            // Siri 透镜展开时：胶囊原有内容全部让位给透镜
            if (siriAlpha > 0.001)
            {
                baseAlpha *= 1 - siriAlpha;
            }

            idleAlpha *= baseAlpha;
            idleDetailAlpha *= baseAlpha;
            compactAlpha *= baseAlpha;
            detailAlpha *= baseAlpha;

            NotificationLayer.Opacity = notificationAlpha * (1 - siriAlpha);
            // 消息展示期间接收点击：点一下即收起（不再"点了没反应"）
            NotificationLayer.IsHitTestVisible = notificationAlpha > 0.5 && siriAlpha < 0.5;

            // Siri 透镜：随展开度淡入并从中心"长出来"
            SiriLensHost.Opacity = siriAlpha;
            SiriLensHost.Visibility = siriAlpha > 0.001 ? Visibility.Visible : Visibility.Collapsed;
            SiriLensHost.IsHitTestVisible = siriAlpha > 0.6;
            double lensScale = 0.62 + 0.38 * Math.Clamp(siri * 1.15, 0, 1);
            SiriLensScale.ScaleX = lensScale;
            SiriLensScale.ScaleY = lensScale;

            IdleView.Opacity = idleAlpha;
            IdleView.IsHitTestVisible = idleAlpha > 0.5;
            IdleScale.ScaleX = 1 + 0.08 * pc;
            IdleScale.ScaleY = 1 + 0.08 * pc;

            IdleDetailLayer.Opacity = idleDetailAlpha;
            IdleDetailLayer.IsHitTestVisible = idleDetailAlpha > 0.5;

            CompactLayer.Opacity = compactAlpha;
            CompactLayer.IsHitTestVisible = compactAlpha > 0.5;

            DetailLayer.Opacity = detailAlpha;
            DetailLayer.IsHitTestVisible = detailAlpha > 0.5;

            // 播放控件只在媒体紧凑态出现：待机时淡出且不参与命中测试，
            // 这样它不会和时间/CPU/RAM 文字叠在一起，也不会吃掉点击。
            // 展开成完整播放面板后同样必须淡出，否则紧凑态的⏮▶⏭会压在音量滑杆上。
            double controlsAlpha = Math.Clamp(pc * 1.8 - 0.25, 0, 1)
                * Math.Clamp(1 - mediaDetail * 1.6, 0, 1)
                * baseAlpha;
            PlaybackControls.Opacity = controlsAlpha;
            PlaybackControls.IsHitTestVisible = controlsAlpha > 0.5;

            ApplyGlassShape(width, height);
        }

        /// <summary>
        /// 把圆角、折射带、染色推给渲染模块。
        /// 阈值 2.5 DIP：动画期间把玻璃重绘次数从「每帧」降到约 1/4，
        /// 玻璃是背景模糊，2.5 DIP 内的形状差异肉眼不可见，但省下大量抓屏开销。
        /// </summary>
        private void ApplyGlassShape(double width, double height)
        {
            // 纯黑 / 磨砂主题没有玻璃层，形状只由 _capsuleClip 负责
            if (_glass == null)
            {
                return;
            }

            // 动画期间每 5 帧才真正刷新玻璃：一次刷新包含抓屏 + 像素处理 + 位图上传，
            // 是逐帧动画里最重的一步。降到约 12Hz 后，慢帧（>16.7ms）数量显著下降，
            // 而玻璃是背景模糊、形状差异在 2.5 DIP 内不可见，观感无损失。
            // 配置参数（染色浓度、圆角）变化时跳过节流，立即重推一次。
            bool force = _glassDirty;
            if (!force && _springRunning && (++_glassFrameSkip % 5) != 0)
            {
                return;
            }

            double shortSide = Math.Min(width, height);
            double radius = Math.Min(Config.CornerRadius * Config.GlobalScale, shortSide * 0.5);
            double bezel = Math.Min(22 * Config.GlobalScale, shortSide * 0.26);

            // 仅媒体展开态启用「上半黑玻璃 / 下半全透明」分区，随展开进度淡入。
            // 空闲展开（大时钟）与紧凑态的 _p 为 0，保持原样。
            double split = Math.Clamp(_d, 0, 1) * Math.Clamp(_p, 0, 1);

            if (!force
                && Math.Abs(width - _lastGlassWidth) < 2.5
                && Math.Abs(height - _lastGlassHeight) < 2.5
                && Math.Abs(radius - _lastGlassRadius) < 0.1
                && Math.Abs(split - _lastGlassSplit) < 0.01
                && _lastGlassWidth > 0)
            {
                return;
            }

            _glassDirty = false;
            _lastGlassWidth = width;
            _lastGlassHeight = height;
            _lastGlassRadius = radius;
            _lastGlassSplit = split;

            // 两种模式共用同一组光学参数：折射、色散、染色与表面不透明度逐项一致
            _glass.Update(effect =>
            {
                effect.CornerRadius = radius;
                effect.BezelWidth = bezel;
                effect.Thickness = bezel * 1.35;
                effect.TintAmount = Config.GlassBlurStrength;
                effect.PanelSplit = split;
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

            if (EffectiveAnimationMs <= 0 || Math.Abs(_p - _target) < 0.0005)
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

            if (detail)
            {
                // 打开面板时立即同步一次进度、音量与播放模式
                SyncPlaybackGlyph();
                SyncVolumeFromSystem();
                RefreshDetailProgress();
            }

            if (EffectiveAnimationMs <= 0)
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
                _springTimer.Start();

                // 形变期间提高玻璃采样帧率，静止时由 StopSpring 降回去
                _glass?.SetFrameRate(24);
            }
        }

        private void StopSpring()
        {
            if (!_springRunning)
            {
                return;
            }

            _springRunning = false;
            _springTimer.Stop();
            _glass?.SetFrameRate(10);

            // 让动画结束后的下一次 ApplyMorph 一定把最终形状推给玻璃层
            _lastGlassWidth = -1;
            _lastGlassHeight = -1;
            _glassFrameSkip = 0;
        }

        private void OnSpringTimerTick(object? sender, EventArgs e)
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
            double response = Math.Max(MinResponse, EffectiveAnimationMs / 1000.0 * 0.5);
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
            double messageTarget = _messageTargetOn ? 1 : 0;
            double introTarget = _introSizeTarget ? 1 : 0;
            double siriTarget = _siriTarget ? 1 : 0;

            for (int i = 0; i < steps; i++)
            {
                double accel = -k * (_p - _target) - c * _v;
                _v += accel * h;
                _p += _v * h;

                double dAccel = -k * (_d - detailTarget) - c * _dv;
                _dv += dAccel * h;
                _d += _dv * h;

                double nAccel = -k * (_n - messageTarget) - c * _nv;
                _nv += nAccel * h;
                _n += _nv * h;

                double iAccel = -k * (_i - introTarget) - c * _iv;
                _iv += iAccel * h;
                _i += _iv * h;

                double sAccel = -k * (_s - siriTarget) - c * _sv;
                _sv += sAccel * h;
                _s += _sv * h;
            }

            bool mainSettled = Math.Abs(_p - _target) < 0.0015 && Math.Abs(_v) < 0.0015;
            bool detailSettled = Math.Abs(_d - detailTarget) < 0.0015 && Math.Abs(_dv) < 0.0015;
            bool messageSettled = Math.Abs(_n - messageTarget) < 0.0015 && Math.Abs(_nv) < 0.0015;
            bool introSettled = Math.Abs(_i - introTarget) < 0.0015 && Math.Abs(_iv) < 0.0015;
            bool siriSettled = Math.Abs(_s - siriTarget) < 0.0015 && Math.Abs(_sv) < 0.0015;

            // 超时兜底：极端负载下也不允许动画无限挂起，否则 Rendering 会持续占满 UI 线程
            bool timedOut = Stopwatch.GetElapsedTime(_springStartTicks) > TimeSpan.FromSeconds(2);

            if (mainSettled && detailSettled && messageSettled && introSettled && siriSettled || timedOut)
            {
                _p = _target;
                _v = 0;
                _d = detailTarget;
                _dv = 0;
                _n = messageTarget;
                _nv = 0;
                _i = introTarget;
                _iv = 0;
                _s = siriTarget;
                _sv = 0;
                StopSpring();
                ApplyMorph(_p);

                // 只有确实处于消息展示状态时才做收尾；否则每次点击展开动画结算
                // 都会被误判成"消息收起"，把 _detail 重置、胶囊瞬间缩回去。
                if (!_messageTargetOn && _messageShowing)
                {
                    FinishMessageCollapse();
                }

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

            // 不在这里重置 _detail：用户点击展开后必须保持展开，
            // 只有用户再次点击、或消息展示结束时才收起。
            AnimateMorphTo(media ? 1 : 0);
            UpdateTexts();
        }

        private SettingsWindow? _settingsWindow;

        /// <summary>右键菜单打开时把当前偏移量显示在标题上，便于边听边调。</summary>
        private void OnIslandMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu || menu.Items.Count < 3)
            {
                return;
            }

            string suffix = string.Format("（当前 {0:+0;-0;0}ms）", Config.LyricOffsetMs);
            if (menu.Items[0] is MenuItem earlier)
            {
                earlier.Header = "歌词提前 200ms" + suffix;
            }

            if (menu.Items[1] is MenuItem later)
            {
                later.Header = "歌词延后 200ms" + suffix;
            }
        }

        private void OnLyricEarlier(object sender, RoutedEventArgs e) => NudgeLyricOffset(200);

        private void OnLyricLater(object sender, RoutedEventArgs e) => NudgeLyricOffset(-200);

        private void OnLyricOffsetReset(object sender, RoutedEventArgs e) => NudgeLyricOffset(-Config.LyricOffsetMs);

        /// <summary>调整歌词整体偏移并立即重算当前行，无需重启。</summary>
        private void NudgeLyricOffset(int deltaMs)
        {
            Config.LyricOffsetMs = Config.LyricOffsetMs + deltaMs;
            _lyricIndex = -2;
            SyncLyricPosition();
        }

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_service, _lyrics, _media, _history, _siriConfig, _siri);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        private void OnExit(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        /// <summary>托盘入口：打开设置（复用右键菜单逻辑，已打开时前置）。</summary>
        internal void ShowSettingsFromTray() => OnOpenSettings(this, new RoutedEventArgs());

        /// <summary>托盘入口：退出程序。</summary>
        internal void ExitFromTray() => Application.Current.Shutdown();

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

            TrackLabel.Text = _media.Title;
            ArtistLabel.Text = string.IsNullOrWhiteSpace(_media.Artist) ? "未知歌手" : _media.Artist;

            double durationSeconds = Math.Max(0, _media.Duration.TotalSeconds);
            _updatingSeek = true;
            try
            {
                SeekSlider.Maximum = Math.Max(1, durationSeconds);
                SeekSlider.IsEnabled = durationSeconds > 0;
                if (durationSeconds <= 0)
                {
                    SeekSlider.Value = 0;
                }
            }
            finally
            {
                _updatingSeek = false;
            }

            bool media = _media.HasSession && !string.IsNullOrWhiteSpace(_media.Title);
            SetMediaMode(media);
        }

        /// <summary>把播放/暂停图标同步为当前状态；点击后的短暂窗口内不覆盖乐观值。</summary>
        private void SyncPlaybackGlyph()
        {
            if (DateTime.UtcNow >= _playGlyphLockUntil)
            {
                SetCompactPlayGlyph(_media.IsPlaying);
                SetDetailPlayGlyph(_media.IsPlaying);
            }

            SyncModeGlyphs();
        }

        /// <summary>紧凑态的播放/暂停：切换矢量图层，避免文字字形在不同 DPI 下被裁切。</summary>
        private void SetCompactPlayGlyph(bool playing)
        {
            CompactPlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            CompactPauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>展开面板的播放/暂停：切换两个矢量图层的可见性（实心三角 / 双竖条）。</summary>
        private void SetDetailPlayGlyph(bool playing)
        {
            DetailPlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            DetailPauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>随机 / 循环按钮的高亮状态（Apple Music 用粉色强调）。</summary>
        private void SyncModeGlyphs()
        {
            ShuffleButton.Foreground = _media.IsShuffleActive ? AccentPink : DimWhite;

            bool repeatOn = _media.RepeatMode != MediaRepeatMode.None;
            RepeatButton.Foreground = repeatOn ? AccentPink : DimWhite;

            // 单曲循环在环内显示 1，对应 SF Symbols 的 repeat.1
            RepeatOneBadge.Visibility = _media.RepeatMode == MediaRepeatMode.One
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 律动条只在播放时保持全亮
            DetailWaveform.Opacity = _media.IsPlaying ? 1 : 0.4;
        }

        private void OnTrackChanged()
        {
            _currentLrc = null;
            _lyricIndex = -2;
            _seeking = false;
            _detailTick = 0;

            _updatingSeek = true;
            try
            {
                SeekSlider.Value = 0;
            }
            finally
            {
                _updatingSeek = false;
            }

            ElapsedText.Text = "0:00";
            RemainingText.Text = "–:–";

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

                // 双重校验：即使取消信号来得稍晚，曲目已经变了也不能把上一首的歌词套上来
                if (ct.IsCancellationRequested
                    || !string.Equals(_media.Title, title, StringComparison.Ordinal)
                    || !string.Equals(_media.Artist, artist, StringComparison.Ordinal))
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
        /// 按播放位置同步当前歌词行（20ms 采样、严格按时间戳切换，整体误差 &lt;100ms）。
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
            TimeSpan lyricTime = position + TimeSpan.FromMilliseconds(Config.LyricOffsetMs);

            // 注意：TimeSpan.MinValue 取 Duration 会溢出，必须先排除首次采样
            if (_lastLyricPosition != TimeSpan.MinValue
                && (position - _lastLyricPosition).Duration() > TimeSpan.FromSeconds(1.5))
            {
                _lyricIndex = -2; // 位置跳变：强制重新定位
            }

            _lastLyricPosition = position;

            // 严格按时间戳切换（不再提前）：提前量会让短间隔的句子"抢跑"到下一句。
            // 用户可用 LyricOffsetMs 微调整首歌词的整体偏移（正值 = 歌词提前显示）。
            int index = doc.LineAt(lyricTime);

            // 逐字高亮：同一行内也要按时间推进，所以行没换时不能提前返回。
            if (index == _lyricIndex)
            {
                UpdateKaraokeHighlight(doc, lyricTime, index);
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

            UpdateKaraokeHighlight(doc, lyricTime, index);
        }

        /// <summary>
        /// 逐字（卡拉 OK）高亮：把当前行按播放进度切成「已唱 / 未唱」两段并着色。
        /// 没有逐字时间戳时退化为整行统一颜色（不做高亮）。
        /// </summary>
        private void UpdateKaraokeHighlight(LrcDocument doc, TimeSpan lyricTime, int lineIndex)
        {
            if (_karaoke.Count == 0 || lineIndex < 0)
            {
                return;
            }

            var line = doc.KaraokeAt(lyricTime, lineIndex);
            if (line == null || line.Words.Count == 0)
            {
                return;
            }

            // 文本与当前行不一致时说明刚切行，等 SwapLyric 之后再更新
            if (!string.Equals(line.Text, _currentLyricText, StringComparison.Ordinal))
            {
                return;
            }

            var (sung, unsung) = line.SplitAt(lyricTime);

            foreach (var pair in _karaoke)
            {
                // 只更新当前可见的那一层（A/B 双缓冲里隐藏的那个不用管）
                if (pair.Key.Opacity > 0.01)
                {
                    pair.Value.Update(sung, unsung);
                }
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

        private void AnimateLyricRow(
            TextBlock incoming, TextBlock outgoing,
            TranslateTransform inTrans, TranslateTransform outTrans, string text)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // 系统关闭「显示动画」时不做歌词滚动，直接切换文本
            double durationMs = SystemParameters.ClientAreaAnimation ? 220 : 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                outgoing.BeginAnimation(OpacityProperty, Fade(outgoing.Opacity, 0, durationMs, ease));
                outTrans.BeginAnimation(TranslateTransform.YProperty, Fade(outTrans.Y, -10, durationMs, ease));
                incoming.BeginAnimation(OpacityProperty, null);
                SetLyricText(incoming, string.Empty);
                incoming.Opacity = 0;
                return;
            }

            SetLyricText(incoming, text);
            inTrans.Y = 10;
            incoming.BeginAnimation(OpacityProperty, Fade(0, 1, durationMs, ease));
            inTrans.BeginAnimation(TranslateTransform.YProperty, Fade(10, 0, durationMs, ease));

            outgoing.BeginAnimation(OpacityProperty, Fade(outgoing.Opacity, 0, durationMs, ease));
            outTrans.BeginAnimation(TranslateTransform.YProperty, Fade(outTrans.Y, -10, durationMs, ease));
        }

        /// <summary>
        /// 写入歌词文本。
        /// 带逐字高亮的 TextBlock 不能直接设 Text（会把 Inline 结构冲掉），
        /// 统一走 KaraokeLyricText 写入。
        /// </summary>
        private void SetLyricText(TextBlock block, string text)
        {
            if (_karaoke.TryGetValue(block, out var karaoke))
            {
                karaoke.SetWholeText(text);
                return;
            }

            block.Text = text;
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

            SetLyricText(active, text);
            active.Opacity = string.IsNullOrEmpty(text) ? 0 : 1;
            SetLyricText(idle, string.Empty);
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
            SetCompactPlayGlyph(willPlay);
            SetDetailPlayGlyph(willPlay);
            _playGlyphLockUntil = DateTime.UtcNow.AddSeconds(1.5);

            await _media.TogglePlayPauseAsync();

            // 命令返回后立刻按最新状态收敛一次；仍与预期不符则等心跳兜底
            SetCompactPlayGlyph(_media.IsPlaying);
            SetDetailPlayGlyph(_media.IsPlaying);
            _playGlyphLockUntil = DateTime.UtcNow.AddMilliseconds(400);
        }

        private void OnSkipPrevious(object sender, RoutedEventArgs e) => _ = _media.SkipPreviousAsync();

        private void OnSkipNext(object sender, RoutedEventArgs e) => _ = _media.SkipNextAsync();

        private async void OnToggleShuffle(object sender, RoutedEventArgs e)
        {
            await _media.ToggleShuffleAsync();
            SyncModeGlyphs();
        }

        private async void OnCycleRepeat(object sender, RoutedEventArgs e)
        {
            await _media.CycleRepeatAsync();
            SyncModeGlyphs();
        }

        #endregion

        #region 展开播放面板的进度与音量

        /// <summary>进度条数值变化：拖拽中只更新时间文字，用户松手或键盘操作时才跳转。</summary>
        private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSeek)
            {
                return;
            }

            UpdateSeekLabels(TimeSpan.FromSeconds(Math.Max(0, e.NewValue)));
            if (!_seeking)
            {
                CommitSeek();
            }
        }

        private void CommitSeek()
        {
            if (_media.Duration <= TimeSpan.Zero)
            {
                return;
            }

            TimeSpan target = TimeSpan.FromSeconds(
                Math.Clamp(SeekSlider.Value, 0, SeekSlider.Maximum));
            _ = _media.SeekAsync(target);
        }

        /// <summary>
        /// 键盘无障碍操作：面板展开后
        /// 空格 = 播放/暂停，←/→ = 快退/快进 5 秒，↑/↓ = 音量 ±5%，Esc = 收起。
        /// </summary>
        private void OnIslandKeyDown(object sender, KeyEventArgs e)
        {
            // Siri 透镜展开时优先响应 Esc / 空格（停止生成）
            if (_siriTarget)
            {
                if (e.Key == Key.Escape)
                {
                    CollapseSiri();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Space && _siri?.Snapshot.IsBusy == true)
                {
                    _siri.StopGeneration();
                    e.Handled = true;
                    return;
                }
            }

            if (!_detail || _messageShowing || !_mediaMode)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    OnTogglePlay(this, new RoutedEventArgs());
                    break;
                case Key.Left:
                    NudgeSeek(-5);
                    break;
                case Key.Right:
                    NudgeSeek(5);
                    break;
                case Key.Up:
                    NudgeVolume(5);
                    break;
                case Key.Down:
                    NudgeVolume(-5);
                    break;
                case Key.Escape:
                    SetDetail(false);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        /// <summary>键盘快进/快退：立即更新滑块与时间文字，再向播放器提交跳转。</summary>
        private void NudgeSeek(double seconds)
        {
            if (_media.Duration <= TimeSpan.Zero)
            {
                return;
            }

            double target = Math.Clamp(
                _media.Position.TotalSeconds + seconds,
                0,
                _media.Duration.TotalSeconds);

            _updatingSeek = true;
            try
            {
                SeekSlider.Value = target;
            }
            finally
            {
                _updatingSeek = false;
            }

            UpdateSeekLabels(TimeSpan.FromSeconds(target));
            _ = _media.SeekAsync(TimeSpan.FromSeconds(target));
        }

        /// <summary>键盘音量增减：写入同一个滑杆，复用系统的音量同步逻辑。</summary>
        private void NudgeVolume(double delta)
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        }

        /// <summary>音量滑杆写入系统主音量（0..100 → 0..1）。</summary>
        private void OnVolumeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolume)
            {
                return;
            }

            SystemVolumeService.SetVolume(e.NewValue / 100.0);
            SetVolumeGlyph(e.NewValue <= 0.5);
        }

        /// <summary>从系统读取一次当前音量（展开面板时同步）。</summary>
        private void SyncVolumeFromSystem()
        {
            if (_volumeDragging)
            {
                return;
            }

            _updatingVolume = true;
            try
            {
                double volume = SystemVolumeService.GetVolume() * 100;
                VolumeSlider.IsEnabled = SystemVolumeService.IsAvailable;
                VolumeSlider.Opacity = SystemVolumeService.IsAvailable ? 1 : 0.4;
                VolumeSlider.Value = volume;
                SetVolumeGlyph(volume <= 0.5);
            }
            finally
            {
                _updatingVolume = false;
            }
        }

        /// <summary>静音 / 正常音量：切换喇叭矢量图（对应 SF Symbols 的 speaker.slash / speaker.wave.2）。</summary>
        private void SetVolumeGlyph(bool muted)
        {
            VolumeSpeaker.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
            VolumeMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>展开面板打开期间按 100ms 节流刷新进度与时间文字。</summary>
        private void UpdateDetailProgress()
        {
            if (!_mediaMode || !_detail || _messageShowing)
            {
                return;
            }

            if ((_detailTick++ % 5) != 0)
            {
                return;
            }

            RefreshDetailProgress();
        }

        private void RefreshDetailProgress()
        {
            TimeSpan position = _media.Position;
            double duration = Math.Max(0, _media.Duration.TotalSeconds);
            bool seekable = duration > 0;

            _updatingSeek = true;
            try
            {
                SeekSlider.Maximum = Math.Max(1, duration);
                // 时长未知（直播 / 播放器不回报时长）时进度条不可拖、压暗，避免出现"拖到 100% 却没反应"
                SeekSlider.IsEnabled = seekable;

                if (!_seeking)
                {
                    SeekSlider.Value = seekable
                        ? Math.Clamp(position.TotalSeconds, 0, SeekSlider.Maximum)
                        : 0;
                    UpdateSeekLabels(position, _media.Duration);
                }
            }
            finally
            {
                _updatingSeek = false;
            }

            // 音量每秒同步一次，避免与用户拖拽打架
            if ((_volumeTick++ % 10) == 0)
            {
                SyncVolumeFromSystem();
            }
        }

        private void UpdateSeekLabels(TimeSpan position, TimeSpan? durationOverride = null)
        {
            TimeSpan duration = durationOverride ?? _media.Duration;
            if (duration <= TimeSpan.Zero)
            {
                // 没有总时长：只显示已播放时间，右侧用占位符而不是"−0:00"
                ElapsedText.Text = FormatClock(position);
                RemainingText.Text = "–:–";
                return;
            }

            TimeSpan remaining = duration > position ? duration - position : TimeSpan.Zero;
            ElapsedText.Text = FormatClock(position);
            RemainingText.Text = "-" + FormatClock(remaining);
        }

        private static string FormatClock(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss")
                : time.ToString(@"m\:ss");
        }

        #endregion

        #region 停靠定位与拖动

        /// <summary>
        /// 停靠：水平居中于工作区，垂直贴屏幕物理顶（参考图贴顶），再叠加配置偏移。
        ///
        /// 多屏 / 混合缩放：不用 <c>SystemParameters.WorkArea</c>（它只返回主屏），
        /// 改为按配置指定（或自动推断）的那一块显示器来算位置，
        /// 并用那块屏自己的 DPI 做换算，避免在 150% + 100% 混合缩放下算错坐标。
        /// </summary>
        private void Reposition()
        {
            Rect work = TargetWorkArea().WorkArea;
            double centeredLeft = work.Left + (work.Width - Width) * 0.5;
            double left = Math.Round(centeredLeft + Config.ScreenOffsetX);

            // 安全边距：左右各留 16 DIP，避免拖到屏幕边缘贴框
            double minLeft = work.Left + 16;
            double maxLeft = work.Right - Width - 16;
            Left = maxLeft >= minLeft ? Math.Clamp(left, minLeft, maxLeft) : left;

            // 贴屏幕物理顶（y=0，压住顶部任务栏，与参考图一致）；不用 work.Top，避免被推下去
            Top = Math.Round(Config.ScreenOffsetY);
        }

        /// <summary>
        /// 岛应该停在哪块屏上。
        /// 配置了 MonitorIndex 就用那一块；没配置就跟随岛当前所在的显示器
        /// （这样在多屏之间拖动时，水平居中会按新屏的工作区重算）。
        /// </summary>
        private MonitorInfo TargetWorkArea()
        {
            var monitors = MonitorService.GetMonitors();
            if (monitors.Count == 0)
            {
                return new MonitorInfo
                {
                    IsPrimary = true,
                    WorkArea = SystemParameters.WorkArea,
                    DpiScale = 1.0,
                };
            }

            // 指定了屏：按序号取；序号越界（拔掉了显示器）时退回主屏
            if (Config.MonitorIndex >= 0)
            {
                if (Config.MonitorIndex < monitors.Count)
                {
                    return monitors[Config.MonitorIndex];
                }

                foreach (var monitor in monitors)
                {
                    if (monitor.IsPrimary)
                    {
                        return monitor;
                    }
                }

                return monitors[0];
            }

            // 自动：跟随岛当前所在位置
            var current = MonitorService.GetMonitorAt(new Point(Left + Width * 0.5, Top + Height * 0.5));
            return current;
        }

        private void PersistScreenOffset()
        {
            Rect work = TargetWorkArea().WorkArea;
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
                if (_messageShowing)
                {
                    // 点击正在展示的消息：立即平滑收起，并清掉排队中的后续消息
                    DismissMessages();
                }
                else if (_siriTarget)
                {
                    // Siri 透镜已展开：点胶囊收起它（光带先收拢变暗再缩回）
                    CollapseSiri();
                    Reposition();
                }
                else if (_siri != null && IsSiriDotHit(e.GetPosition(this)))
                {
                    // 点击胶囊右端的 Siri 光点：展开透镜并开始收音
                    TriggerSiriVoice();
                }
                else
                {
                    // 左键点击胶囊本体：待机时展开大时钟，播放时展开曲目详情（按钮点击已被过滤）
                    SetDetail(!_detail);
                    Reposition();
                }
            }
        }

        /// <summary>
        /// 判断点击是否落在可交互控件上（按钮 / 输入框）。
        ///
        /// 注意：歌词用 Inline / Run 渲染，鼠标命中的 OriginalSource 可能是 Run 这类
        /// <see cref="ContentElement"/> —— 它不是 Visual，直接调 <see cref="VisualTreeHelper.GetParent"/>
        /// 会抛 InvalidOperationException。这里按节点类型分别走视觉树与内容树。
        /// </summary>
        private static bool IsInteractive(DependencyObject? source)
        {
            DependencyObject? node = source;
            int guard = 0;

            // guard 只防异常数据导致死循环，正常 DOM 深度远小于 256
            while (node != null && guard++ < 256)
            {
                if (node is ButtonBase || node is TextBox)
                {
                    return true;
                }

                node = ParentOf(node);
            }

            return false;
        }

        /// <summary>取父节点：Visual 走视觉树，ContentElement（Run / Inline）走内容树。</summary>
        private static DependencyObject? ParentOf(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(node);
            }

            if (node is ContentElement content)
            {
                return ContentOperations.GetParent(content)
                    ?? (content as FrameworkContentElement)?.Parent;
            }

            return null;
        }

        #endregion

        #region 文案

        private void UpdateTexts()
        {
            int count = _feed?.Notifications.Count ?? 0;

            DateTime now = DateTime.Now;
            IdleTime.Text = now.ToString("HH:mm");
            IdleHint.Text = count > 0 ? string.Format("{0} 条新通知", count) : "待机";

            IdleDetailTime.Text = IdleTime.Text;
            IdleDetailDate.Text = now.ToString("M月d日 dddd", ZhCulture);

            ApplyIdleVisibility();
            UpdateMetricsText();
            RemeasureIdle();
        }

        /// <summary>按用户开关决定空闲态各段的显隐，分隔线只在两侧都有内容时出现。</summary>
        private void ApplyIdleVisibility()
        {
            bool clock = Config.ShowClock;
            bool hint = Config.ShowIdleHint;
            bool metrics = Config.ShowSystemMetrics;

            IdleTime.Visibility = VisibleIf(clock);
            IdleHint.Visibility = VisibleIf(hint);
            IdleMetrics.Visibility = VisibleIf(metrics);
            IdleDetailMetrics.Visibility = VisibleIf(metrics);

            IdleClockDivider.Visibility = VisibleIf(clock && hint);
            IdleMetricsDivider.Visibility = VisibleIf(metrics && (clock || hint));
        }

        private static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 实测空闲态内容宽度并同步胶囊尺寸：用户增减显示项后胶囊自动收缩/伸展，
        /// 永远不会出现文字被裁切或留白过多。
        /// </summary>
        private void RemeasureIdle()
        {
            IdleView.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double measured = IdleView.DesiredSize.Width + 16;

            if (Math.Abs(measured - _idleMinWidth) < 0.5)
            {
                return;
            }

            _idleMinWidth = measured;

            if (_springRunning)
            {
                // 动画期间交给弹簧逐帧应用，避免与形变互相打断
                return;
            }

            ApplyWindowSize();
            ApplyMorph(_p);

            if (!_dragging)
            {
                Reposition();
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            StopSpring();
            _clockTimer.Stop();
            _lyricTimer.Stop();
            _topmostTimer.Stop();
            _messageHoldTimer.Stop();

            _service.Changed -= OnConfigChanged;
            _media.StateChanged -= OnMediaStateChanged;
            _lyrics.ManualImported -= OnLyricsManualImported;
            _lyricLookupCts?.Cancel();
            SystemEvents.SessionSwitch -= OnSessionSwitch;

            _feed.Dispose();
            _bridge?.Dispose();
            _history.Dispose();

            if (_systemTheme != null)
            {
                _systemTheme.ThemeChanged -= OnSystemThemeChanged;
                _systemTheme.Dispose();
                _systemTheme = null;
            }

            _metrics.Updated -= OnMetricsUpdated;
            _metrics.Dispose();

            DisposeSiri();

            _glass?.Dispose();
            _glass = null;

            base.OnClosed(e);
        }
    }
}
