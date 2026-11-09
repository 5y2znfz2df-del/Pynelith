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
        }

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

            var config = NewGroup(page, "配置");
            AddNote(config, "配置文件", _service.ConfigPath);

            return page;
        }

        private UIElement BuildAppearancePage()
        {
            var page = NewPage();

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

        private UIElement BuildSizePage()
        {
            var page = NewPage();

            var collapsed = NewGroup(page, "空闲尺寸");
            AddStepper(collapsed, "宽度", 100, 480, 2, 0,
                () => Config.CollapsedWidth, v => Config.CollapsedWidth = v);
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

            var display = NewGroup(page, "展示");
            AddStepper(display, "通知停留 (ms)", 1000, 15000, 500, 0,
                () => Config.NotificationDurationMs, v => Config.NotificationDurationMs = (int)Math.Round(v));

            var blacklist = NewGroup(page, "黑名单");
            AddMultiline(blacklist, "每行一个应用名，命中的通知直接丢弃");

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
            AddButton(lyrics, "导入 LRC 歌词…", OnImportLrc);
            AddNote(lyrics, "歌词缓存", LyricsService.LyricsDirectory);
            AddNote(lyrics, "匹配方式", "按正在播放曲目的「标题 + 歌手」自动匹配，可手动导入覆盖");

            var control = NewGroup(page, "播放控制");
            AddNote(control, "控制通道", "SMTC 系统媒体传输控件（全局会话自动扫描）");
            AddNote(control, "可用操作", "播放 / 暂停、上一首、下一首；点击岛本体 = 播放/暂停");

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

        private void AddMultiline(StackPanel rows, string hint)
        {
            var box = new TextBox
            {
                Height = 76,
                AcceptsReturn = true,
                Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
                Foreground = TextPrimary,
                CaretBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                FontSize = 12,
                Margin = new Thickness(4, 6, 4, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            box.TextChanged += (_, _) =>
            {
                if (_loading)
                {
                    return;
                }

                var names = new List<string>();
                foreach (string line in box.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        names.Add(trimmed);
                    }
                }

                Config.NotificationBlacklist = names;
            };

            rows.Children.Add(box);

            _refreshers.Add(() =>
            {
                box.Text = string.Join(Environment.NewLine, Config.NotificationBlacklist);
            });

            rows.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 10.5,
                Foreground = TextSecondary,
                Margin = new Thickness(14, 0, 14, 8),
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
