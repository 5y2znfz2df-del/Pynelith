using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pynelith.Island.Configuration;
using Pynelith.Island.Services;
using Pynelith.Island.Services.Lyrics;
using Pynelith.Island.Services.Media;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 独立设置窗口（macOS 系统设置同款版式）：左侧分页侧边栏，右侧分组卡片行。
    /// 所有控件直接读写唯一配置实例：写属性 → 配置变更 → 岛窗口实时应用并推给渲染模块。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly Brush TextPrimary = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly Brush TextSecondary = new SolidColorBrush(Color.FromArgb(0x98, 0x98, 0x98, 0x9F));
        private static readonly Brush CardBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2D));
        private static readonly Brush DividerBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3D));
        private static readonly Brush FieldBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3D));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF));

        private readonly ConfigService _service;
        private readonly LyricsService _lyrics;
        private readonly MediaSessionService _media;
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly Dictionary<string, UIElement> _pages = new Dictionary<string, UIElement>();
        private readonly Dictionary<string, Button> _sideButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, string> _sideTitles = new Dictionary<string, string>();
        private string _currentPage = string.Empty;
        private bool _loading;

        private IslandConfig Config => _service.Config;

        public SettingsWindow(ConfigService service, LyricsService lyrics, MediaSessionService media)
        {
            _service = service;
            _lyrics = lyrics;
            _media = media;

            InitializeComponent();

            BuildSidebar();
            BuildPages();

            _loading = true;
            foreach (Action refresh in _refreshers)
            {
                refresh();
            }

            _loading = false;

            SelectPage("general");

            StateChanged += OnWindowStateChanged;
        }

        #region 窗口外壳（macOS 交通灯）

        private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

        private void OnMinimizeWindow(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void OnZoomWindow(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        /// <summary>标题栏拖动；双击空白处等同于绿色按钮（放大 / 还原）。</summary>
        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || IsButtonSource(e.OriginalSource as DependencyObject))
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                OnZoomWindow(sender, e);
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标在拖动开始前已释放：忽略即可。
            }
        }

        private static bool IsButtonSource(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase)
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        /// <summary>最大化时去掉圆角与描边，避免圆角在屏幕边缘留下缺口。</summary>
        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            bool maximized = WindowState == WindowState.Maximized;
            WindowFrame.CornerRadius = new CornerRadius(maximized ? 0 : 12);
            WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
            ZoomLight.ToolTip = maximized ? "还原" : "放大";
        }

        #endregion

        #region 侧边栏

        private sealed class PageEntry
        {
            public string Key = string.Empty;
            public string Title = string.Empty;
            public string Glyph = string.Empty;
            public Brush Icon = Brushes.Gray;
        }

        private void BuildSidebar()
        {
            var entries = new[]
            {
                NewEntry("general", "通用", "⚙", LinearGradient("#8E8E93", "#636366")),
                NewEntry("appearance", "外观", "◐", LinearGradient("#BF5AF2", "#8944AB")),
                NewEntry("display", "显示内容", "▤", LinearGradient("#30D158", "#248A3D")),
                NewEntry("size", "尺寸与位置", "⇲", LinearGradient("#64D2FF", "#0A84FF")),
                NewEntry("notify", "通知与提示", "🔔", LinearGradient("#FF6961", "#FF3B30")),
                NewEntry("music", "音乐", "♫", LinearGradient("#FF6482", "#FF2D55")),
            };

            foreach (var entry in entries)
            {
                var icon = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(6),
                    Background = entry.Icon,
                    Child = new TextBlock
                    {
                        Text = entry.Glyph,
                        FontSize = 11,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };

                var label = new TextBlock
                {
                    Text = entry.Title,
                    FontSize = 13,
                    Foreground = TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(9, 0, 0, 0),
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(icon);
                row.Children.Add(label);

                var button = new Button
                {
                    Style = (Style)FindResource("SideButton"),
                    Content = row,
                    Tag = entry.Key,
                };
                button.Click += OnSideClick;

                _sideButtons[entry.Key] = button;
                _sideTitles[entry.Key] = entry.Title;
                SideStack.Children.Add(button);
            }
        }

        private static PageEntry NewEntry(string key, string title, string glyph, Brush icon)
            => new PageEntry { Key = key, Title = title, Glyph = glyph, Icon = icon };

        private static Brush LinearGradient(string from, string to)
            => new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(from),
                (Color)ColorConverter.ConvertFromString(to),
                new Point(0, 0),
                new Point(1, 1));

        private void OnSideClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                SelectPage(key);
            }
        }

        private void SelectPage(string key)
        {
            if (!_pages.ContainsKey(key) || _currentPage == key)
            {
                return;
            }

            _currentPage = key;

            foreach (var pair in _sideButtons)
            {
                pair.Value.Background = pair.Key == key
                    ? (Brush)FindResource("AccentBrush")
                    : Brushes.Transparent;
            }

            PageHost.Children.Clear();
            PageHost.Children.Add(_pages[key]);
            PageTitle.Text = _sideTitles[key];
        }

        #endregion

        #region 页面构建

        private void BuildPages()
        {
            _pages["general"] = BuildGeneralPage();
            _pages["appearance"] = BuildAppearancePage();
            _pages["display"] = BuildDisplayPage();
            _pages["size"] = BuildSizePage();
            _pages["notify"] = BuildNotifyPage();
            _pages["music"] = BuildMusicPage();
        }

        private UIElement BuildGeneralPage()
        {
            var page = NewPage();

            var island = NewGroup(page, "灵动岛");
            AddStepper(island, "动画时长 (ms)", 0, 800, 20, 0,
                () => Config.AnimationDurationMs, v => Config.AnimationDurationMs = (int)Math.Round(v));
            AddNote(island, "产品定位", "桌面顶部的轻量状态胶囊：时间 + CPU/内存；有音乐时自动接管显示");

            var config = NewGroup(page, "配置");
            AddNote(config, "配置文件", _service.ConfigPath);

            return page;
        }

        private UIElement BuildAppearancePage()
        {
            var page = NewPage();

            var theme = NewGroup(page, "主题");
            AddSegmented(theme, "界面主题", ThemeStyles.All,
                () => Config.Theme, v => Config.Theme = v);
            AddNote(theme, "液态玻璃", "抓屏折射 + 色散，最接近真实玻璃；会占用少量 CPU");
            AddNote(theme, "纯黑 / 磨砂", "静态底色，不做屏幕采样，适合低配机器或省电场景");

            var compat = NewGroup(page, "兼容");
            AddToggle(compat, "兼容模式（截图与录屏可见）",
                () => Config.ScreenshotCompatible, v => Config.ScreenshotCompatible = v);
            AddDescription(compat, "开启后改用可录制的玻璃外观：轮廓与窄高光都在，截图/录屏能看到灵动岛；实时折射会暂停。");

            var glass = NewGroup(page, "玻璃");
            AddStepper(glass, "玻璃模糊强度", 0, 1, 0.01, 2,
                () => Config.GlassBlurStrength, v => Config.GlassBlurStrength = v);
            AddStepper(glass, "圆角半径", 0, 60, 1, 0,
                () => Config.CornerRadius, v => Config.CornerRadius = v);

            var layout = NewGroup(page, "排版");
            AddStepper(layout, "全局缩放", 0.6, 1.8, 0.05, 2,
                () => Config.GlobalScale, v => Config.GlobalScale = v, "×");

            return page;
        }

        private UIElement BuildDisplayPage()
        {
            var page = NewPage();

            var content = NewGroup(page, "空闲态内容");
            AddToggle(content, "显示时间",
                () => Config.ShowClock, v => Config.ShowClock = v);
            AddToggle(content, "显示待机文字",
                () => Config.ShowIdleHint, v => Config.ShowIdleHint = v);
            AddToggle(content, "显示 CPU / 内存占用",
                () => Config.ShowSystemMetrics, v => Config.ShowSystemMetrics = v);
            AddNote(content, "自适应宽度", "胶囊按实际内容自动收缩/展开，关闭全部选项后只保留提示点");

            var metrics = NewGroup(page, "资源监控");
            AddNote(metrics, "数据来源", "Win32 GetSystemTimes / GlobalMemoryStatusEx，每秒刷新一次");
            AddNote(metrics, "采样开销", "只做两次系统调用，不依赖性能计数器；关闭后完全停止采样");
            AddNote(metrics, "高负载提示", "CPU 或内存超过 85% 时数值转为橙红色");

            return page;
        }

        private UIElement BuildSizePage()
        {
            var page = NewPage();

            var collapsed = NewGroup(page, "空闲尺寸");
            AddStepper(collapsed, "宽度", 100, 480, 2, 0,
                () => Config.CollapsedWidth, v => Config.CollapsedWidth = v);
            AddNote(collapsed, "自适应", "实际宽度不会小于内容宽度，避免时间/监控文字被裁切");
            AddStepper(collapsed, "高度", 20, 96, 1, 0,
                () => Config.CollapsedHeight, v => Config.CollapsedHeight = v);

            var expanded = NewGroup(page, "媒体尺寸");
            AddStepper(expanded, "宽度", 160, 900, 4, 0,
                () => Config.ExpandedWidth, v => Config.ExpandedWidth = v);
            AddNote(expanded, "高度", "始终与空闲态一致，避免遮挡视野");

            var offset = NewGroup(page, "屏幕偏移");
            AddStepper(offset, "水平偏移 X", -800, 800, 2, 0,
                () => Config.ScreenOffsetX, v => Config.ScreenOffsetX = v);
            AddStepper(offset, "垂直偏移 Y", -40, 300, 1, 0,
                () => Config.ScreenOffsetY, v => Config.ScreenOffsetY = v);

            return page;
        }

        private UIElement BuildNotifyPage()
        {
            var page = NewPage();

            var takeover = NewGroup(page, "接管");
            AddToggle(takeover, "接管全部系统通知",
                () => Config.TakeoverNotifications, v => Config.TakeoverNotifications = v);
            AddNote(takeover, "接管状态", SystemNotificationBridge.Status);
            AddNote(takeover, "说明", "接管只决定是否撤下系统原生通知；消息始终会在岛内展示");

            var link = new TextBlock
            {
                Text = "未授权？点此打开「系统设置 → 通知 → 通知访问」允许 Pynelith",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF)),
                Cursor = Cursors.Hand,
                Margin = new Thickness(14, 2, 14, 10),
            };
            link.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // 打不开设置页时忽略。
                }
            };
            takeover.Children.Add(link);

            var display = NewGroup(page, "消息展示");
            AddStepper(display, "消息停留 (ms)", 1500, 15000, 500, 0,
                () => Config.NotificationDurationMs, v => Config.NotificationDurationMs = (int)Math.Round(v));
            AddNote(display, "展示方式", "通知到达时岛自动展开并完整显示正文，停留后平滑收回");
            AddNote(display, "过滤规则", "无黑白名单：所有系统通知都会展示");
            AddNote(display, "授权", "若列表为空，请在上方打开「通知访问」允许 Pynelith 读取通知");

            return page;
        }

        private UIElement BuildMusicPage()
        {
            var page = NewPage();

            var lyrics = NewGroup(page, "歌词");
            AddToggle(lyrics, "显示歌词行",
                () => Config.LyricsEnabled, v => Config.LyricsEnabled = v);
            AddToggle(lyrics, "网易云音乐歌词源",
                () => Config.LyricsSourceNetease, v => Config.LyricsSourceNetease = v);
            AddToggle(lyrics, "QQ音乐歌词源",
                () => Config.LyricsSourceQQ, v => Config.LyricsSourceQQ = v);
            AddToggle(lyrics, "酷狗音乐歌词源",
                () => Config.LyricsSourceKugou, v => Config.LyricsSourceKugou = v);
            AddStepper(lyrics, "时间偏移 (ms)", -2000, 2000, 50, 0,
                () => Config.LyricOffsetMs, v => Config.LyricOffsetMs = (int)v);
            AddButton(lyrics, "导入 LRC 歌词…", OnImportLrc);
            AddNote(lyrics, "歌词缓存", LyricsService.LyricsDirectory);
            AddNote(lyrics, "时间偏移", "正值让歌词提前出现，用于对齐整体偏快/偏慢的歌词源");
            AddNote(lyrics, "匹配方式", "按正在播放曲目的「标题 + 歌手」自动匹配，可手动导入覆盖");

            var control = NewGroup(page, "播放控制");
            AddNote(control, "控制通道", "SMTC 系统媒体传输控件（全局会话自动扫描）");
            AddNote(control, "可用操作", "播放 / 暂停、上一首、下一首；点击岛本体 = 展开详情");
            AddNote(control, "显示时机", "仅在检测到正在播放的会话时出现；播放器退出后自动回到待机");

            return page;
        }

        private void OnImportLrc()
        {
            if (string.IsNullOrWhiteSpace(_media.Title))
            {
                MessageBox.Show(this,
                    "请先在任意播放器播放一首歌，再把对应的 LRC 歌词导入到该曲目。",
                    "Pynelith", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入 LRC 歌词",
                Filter = "LRC 歌词|*.lrc;*.txt|所有文件|*.*",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                _lyrics.ImportLrc(dialog.FileName, _media.Title, _media.Artist);
                MessageBox.Show(this,
                    string.Format("已关联到《{0}》，歌词立即生效。", _media.Title),
                    "Pynelith", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导入失败：" + ex.Message, "Pynelith",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region 行控件

        private static StackPanel NewPage() => new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        private StackPanel NewGroup(StackPanel page, string header)
        {
            page.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                Margin = new Thickness(2, 16, 0, 6),
            });

            var rows = new StackPanel();
            var card = new Border
            {
                Background = CardBrush,
                CornerRadius = new CornerRadius(10),
                Child = rows,
            };

            page.Children.Add(card);
            return rows;
        }

        private Grid NewRow(StackPanel rows, string label)
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(new Border { Height = 1, Background = DividerBrush, Margin = new Thickness(14, 0, 0, 0) });
            }

            var row = new Grid { MinHeight = 42, Margin = new Thickness(14, 0, 14, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            });

            rows.Children.Add(row);
            return row;
        }

        private void AddToggle(StackPanel rows, string label, Func<bool> read, Action<bool> write)
        {
            var row = NewRow(rows, label);

            var toggle = new CheckBox { Style = (Style)FindResource("IosToggle"), VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += (_, _) => { if (!_loading) write(true); };
            toggle.Unchecked += (_, _) => { if (!_loading) write(false); };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);

            _refreshers.Add(() => toggle.IsChecked = read());
        }

        private void AddButton(StackPanel rows, string label, Action onClick)
        {
            var row = NewRow(rows, label);

            var button = new Button
            {
                Content = label,
                Cursor = Cursors.Hand,
                Foreground = TextPrimary,
                FontSize = 12,
                Padding = new Thickness(14, 5, 14, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Template = DarkButtonTemplate(),
            };
            button.Click += (_, _) => onClick();

            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }

        /// <summary>分段选择器（主题切换用）：一组互斥按钮，选中项高亮。</summary>
        private void AddSegmented(
            StackPanel rows,
            string label,
            (string Key, string Name)[] options,
            Func<string> read,
            Action<string> write)
        {
            var row = NewRow(rows, label);

            var group = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var buttons = new List<(Button Button, string Key)>();

            foreach (var option in options)
            {
                string key = option.Key;
                var button = new Button
                {
                    Content = option.Name,
                    FontSize = 11.5,
                    Foreground = TextPrimary,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(0),
                    Template = SegmentButtonTemplate(),
                };

                button.Click += (_, _) =>
                {
                    if (!_loading)
                    {
                        write(key);
                    }

                    RefreshSegments(buttons, read);
                };

                group.Children.Add(button);
                buttons.Add((button, key));
            }

            Grid.SetColumn(group, 1);
            row.Children.Add(group);

            RefreshSegments(buttons, read);
            _refreshers.Add(() => RefreshSegments(buttons, read));
        }

        private void RefreshSegments(List<(Button Button, string Key)> buttons, Func<string> read)
        {
            string current = read();
            foreach (var item in buttons)
            {
                item.Button.Background = string.Equals(item.Key, current, StringComparison.Ordinal)
                    ? AccentBrush
                    : FieldBrush;
            }
        }

        private static ControlTemplate SegmentButtonTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            factory.SetValue(Border.PaddingProperty, new Thickness(11, 4, 11, 4));
            factory.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };

            var hover = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
            };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
            template.Triggers.Add(hover);

            return template;
        }

        private static ControlTemplate DarkButtonTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            factory.SetValue(Border.BackgroundProperty, FieldBrush);
            factory.SetValue(Border.PaddingProperty, new Thickness(14, 5, 14, 5));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };

            var hover = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
            };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0xFF, 0x47, 0x47, 0x4B))));
            template.Triggers.Add(hover);

            var pressed = new Trigger
            {
                Property = ButtonBase.IsPressedProperty,
                Value = true,
            };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0xFF, 0x23, 0x23, 0x26))));
            template.Triggers.Add(pressed);

            return template;
        }

        private void AddStepper(
            StackPanel rows,
            string label,
            double min,
            double max,
            double step,
            int decimals,
            Func<double> read,
            Action<double> write,
            string suffix = "")
        {
            var row = NewRow(rows, label);

            var valueText = new TextBlock
            {
                FontSize = 12.5,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var field = new Border
            {
                Width = 74,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = FieldBrush,
                Child = valueText,
            };

            var up = new RepeatButton { Style = (Style)FindResource("StepButton"), Content = "▲" };
            var down = new RepeatButton { Style = (Style)FindResource("StepButton"), Content = "▼" };
            up.Delay = 400;
            up.Interval = 80;
            down.Delay = 400;
            down.Interval = 80;

            var buttons = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            buttons.Children.Add(up);
            buttons.Children.Add(new Border { Height = 2 });
            buttons.Children.Add(down);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(field);
            right.Children.Add(buttons);

            Grid.SetColumn(right, 1);
            row.Children.Add(right);

            double current = Math.Clamp(read(), min, max);

            void Commit(double value)
            {
                current = Math.Clamp(Math.Round(value / step) * step, min, max);
                valueText.Text = current.ToString("F" + decimals) + suffix;

                if (!_loading)
                {
                    write(current);
                }
            }

            up.Click += (_, _) => Commit(current + step);
            down.Click += (_, _) => Commit(current - step);

            _refreshers.Add(() =>
            {
                current = Math.Clamp(read(), min, max);
                valueText.Text = current.ToString("F" + decimals) + suffix;
            });
        }

        private void AddNote(StackPanel rows, string label, string note)
        {
            var row = NewRow(rows, label);

            var noteText = new TextBlock
            {
                Text = note,
                FontSize = 11,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380,
            };
            Grid.SetColumn(noteText, 1);
            row.Children.Add(noteText);
        }

        /// <summary>开关下方的整行说明文字（不占行标题列，与系统设置的分组说明一致）。</summary>
        private void AddDescription(StackPanel rows, string text)
        {
            rows.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
                Margin = new Thickness(14, -8, 14, 12),
            });
        }

        #endregion

        private void OnReset(object sender, MouseButtonEventArgs e)
        {
            _loading = true;
            Config.ResetToDefaults();

            foreach (Action refresh in _refreshers)
            {
                refresh();
            }

            _loading = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _service.SaveNow();
            base.OnClosed(e);
        }
    }
}
