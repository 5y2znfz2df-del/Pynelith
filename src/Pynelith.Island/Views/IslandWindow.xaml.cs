using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pynelith.Glass.Effects;
using Pynelith.Island.Configuration;
using Pynelith.Island.Services;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 悬浮动态岛主窗口。
    /// 职责：停靠定位、展开/收起过渡动画、信息展示布局、把配置实时推给渲染模块。
    /// 玻璃能力全部来自 <see cref="LiquidGlassSurface"/>，本文件不包含任何渲染实现。
    /// </summary>
    public partial class IslandWindow : Window
    {
        private readonly ConfigService _service;
        private readonly LiquidGlassSurface _glass;
        private readonly MockFeed _feed;
        private readonly DispatcherTimer _clockTimer;

        private bool _expanded;
        private bool _dragging;
        private bool _pressActive;
        private Point _pressPoint;

        private IslandConfig Config => _service.Config;

        public IslandWindow(ConfigService service)
        {
            _service = service;

            InitializeComponent();

            // ---- 玻璃：只调用渲染模块公开接口 ----
            _glass = LiquidGlassSurface.Install(IslandRoot, new LiquidGlassSurface.LiquidGlassStyle
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

            // ---- 模拟数据 ----
            _feed = new MockFeed(Config);
            NotificationList.ItemsSource = _feed.Notifications;
            NowPlaying.DataContext = _feed.Track;

            _clockTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _clockTimer.Tick += (_, _) => UpdateTexts();

            _service.Changed += OnConfigChanged;
            _feed.Notifications.CollectionChanged += (_, _) => UpdateTexts();
            _feed.Track.PropertyChanged += (_, _) => UpdateLyric();

            Loaded += OnLoaded;
            SizeChanged += OnIslandSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyLayout(resize: true, animate: false);
            UpdateTexts();
            UpdateLyric();
            _clockTimer.Start();
        }

        #region 配置

        private void OnConfigChanged(object? sender, EventArgs e)
        {
            ApplyLayout(resize: true, animate: false);
        }

        /// <summary>
        /// 把当前配置应用到 UI 与渲染模块。
        /// 设置窗口里的任何改动都会走到这里，因此是"改完即时生效"的唯一入口。
        /// </summary>
        private void ApplyLayout(bool resize, bool animate)
        {
            double scale = Config.GlobalScale;
            ContentHost.LayoutTransform = new ScaleTransform(scale, scale);

            LyricsText.Visibility = Config.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;

            if (resize)
            {
                ResizeIsland(_expanded, animate);
            }

            ApplyGlassShape();
            UpdateTexts();

            if (!_dragging)
            {
                Reposition();
            }
        }

        /// <summary>把圆角、边缘折射带、染色强度推给渲染模块。</summary>
        private void ApplyGlassShape()
        {
            double width = Math.Max(ActualWidth, 1);
            double height = Math.Max(ActualHeight, 1);
            double shortSide = Math.Min(width, height);

            // 圆角不能超过短边一半，否则 SDF 会退化。
            double radius = Math.Min(Config.CornerRadius * Config.GlobalScale, shortSide * 0.5);

            // 折射带按短边比例收缩，胶囊形态下不会整块被"掰弯"。
            double bezel = Math.Min(22, shortSide * 0.26);

            _glass.Update(effect =>
            {
                effect.CornerRadius = radius;
                effect.BezelWidth = bezel;
                effect.Thickness = bezel * 1.35;
                effect.TintAmount = Config.GlassBlurStrength;
            });
        }

        #endregion

        #region 尺寸与动画

        private void OnIslandSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyGlassShape();

            if (!_dragging)
            {
                Reposition();
            }
        }

        private (double Width, double Height) TargetSize(bool expanded)
        {
            double scale = Config.GlobalScale;
            double width = expanded ? Config.ExpandedWidth : Config.CollapsedWidth;
            double height = expanded ? Config.ExpandedHeight : Config.CollapsedHeight;

            return (Math.Max(60, Math.Round(width * scale)), Math.Max(24, Math.Round(height * scale)));
        }

        private void ResizeIsland(bool expanded, bool animate)
        {
            var (targetWidth, targetHeight) = TargetSize(expanded);
            int duration = Math.Clamp(Config.AnimationDurationMs, 0, 2000);

            if (!animate || duration == 0)
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(HeightProperty, null);
                Width = targetWidth;
                Height = targetHeight;
                return;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var span = TimeSpan.FromMilliseconds(duration);

            var widthAnimation = new DoubleAnimation(targetWidth, span) { EasingFunction = easing };
            var heightAnimation = new DoubleAnimation(targetHeight, span) { EasingFunction = easing };

            // 动画结束后换回本地值，避免后续手动赋值被动画覆盖。
            widthAnimation.Completed += (_, _) =>
            {
                BeginAnimation(WidthProperty, null);
                Width = targetWidth;
            };
            heightAnimation.Completed += (_, _) =>
            {
                BeginAnimation(HeightProperty, null);
                Height = targetHeight;
            };

            BeginAnimation(WidthProperty, widthAnimation);
            BeginAnimation(HeightProperty, heightAnimation);
        }

        private void SetExpanded(bool expanded, bool animate)
        {
            if (_expanded == expanded)
            {
                return;
            }

            _expanded = expanded;

            Grid incoming = expanded ? ExpandedView : CollapsedView;
            Grid outgoing = expanded ? CollapsedView : ExpandedView;

            outgoing.Visibility = Visibility.Collapsed;
            incoming.Visibility = Visibility.Visible;

            int duration = Math.Clamp(Config.AnimationDurationMs, 0, 2000);
            if (animate && duration > 0)
            {
                incoming.Opacity = 0;
                incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(duration))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            }
            else
            {
                incoming.BeginAnimation(OpacityProperty, null);
                incoming.Opacity = 1;
            }

            ResizeIsland(expanded, animate);
            ApplyGlassShape();
            UpdateTexts();
        }

        private void ToggleExpanded()
        {
            SetExpanded(!_expanded, animate: true);
        }

        private void OnCollapseClick(object sender, RoutedEventArgs e)
        {
            SetExpanded(false, animate: true);
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

        /// <summary>停靠点：工作区顶部居中 + 配置偏移。</summary>
        private void Reposition()
        {
            Rect work = SystemParameters.WorkArea;
            double centeredLeft = work.Left + (work.Width - ActualWidth) * 0.5;

            Left = Math.Round(centeredLeft + Config.ScreenOffsetX);
            Top = Math.Round(work.Top + Config.ScreenOffsetY);
        }

        /// <summary>拖动结束后把落点换算成相对停靠点的偏移并写回配置。</summary>
        private void PersistScreenOffset()
        {
            Rect work = SystemParameters.WorkArea;
            double centeredLeft = work.Left + (work.Width - ActualWidth) * 0.5;

            Config.ScreenOffsetX = Math.Round(Left - centeredLeft);
            Config.ScreenOffsetY = Math.Round(Top - work.Top);
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
            bool moved = Math.Abs(current.X - _pressPoint.X) > 4 || Math.Abs(current.Y - _pressPoint.Y) > 4;
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
            }
        }

        /// <summary>点中按钮等交互控件时不要触发拖动/展开。</summary>
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
            string now = DateTime.Now.ToString("HH:mm");
            int count = _feed?.Notifications.Count ?? 0;

            CollapsedTime.Text = now;
            CollapsedHint.Text = count > 0 ? string.Format("{0} 条新通知", count) : "待机";
            ExpandedSubtitle.Text = string.Format("今天 {0} · {1} 条通知", now, count);
        }

        private void UpdateLyric()
        {
            string lyric = _feed?.Track.Lyric ?? string.Empty;
            LyricsText.Text = string.IsNullOrEmpty(lyric) ? string.Empty : "♪  " + lyric;
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            _service.Changed -= OnConfigChanged;

            _feed.Dispose();
            _glass.Dispose();

            base.OnClosed(e);
        }
    }
}
