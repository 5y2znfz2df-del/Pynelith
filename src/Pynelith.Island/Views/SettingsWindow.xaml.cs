using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pynelith.Island.Configuration;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 独立设置窗口。所有控件直接读写 <see cref="ConfigService"/> 里的唯一配置实例：
    /// 写属性 → 配置变更事件 → 岛窗口把新参数推给渲染模块并重排 → 防抖落盘。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        private static readonly Brush ValueBrush = new SolidColorBrush(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF));

        private readonly ConfigService _service;
        private readonly List<Action> _refreshers = new List<Action>();
        private bool _loading;

        private IslandConfig Config => _service.Config;

        public SettingsWindow(ConfigService service)
        {
            _service = service;

            InitializeComponent();

            PathText.Text = "配置文件：" + _service.ConfigPath;

            _loading = true;
            BuildRows();
            LoadFromConfig();
            _loading = false;
        }

        #region 控件构建

        private void BuildRows()
        {
            AddSlider("全局缩放", 0.6, 1.8, 0.05,
                () => Config.GlobalScale, v => Config.GlobalScale = v, v => v.ToString("0.00") + "×");

            AddSlider("收起宽度", 160, 360, 2,
                () => Config.CollapsedWidth, v => Config.CollapsedWidth = v, v => v.ToString("0"));

            AddSlider("收起高度", 28, 72, 1,
                () => Config.CollapsedHeight, v => Config.CollapsedHeight = v, v => v.ToString("0"));

            AddSlider("展开宽度", 300, 680, 4,
                () => Config.ExpandedWidth, v => Config.ExpandedWidth = v, v => v.ToString("0"));

            AddSlider("展开高度", 160, 420, 4,
                () => Config.ExpandedHeight, v => Config.ExpandedHeight = v, v => v.ToString("0"));

            AddSlider("水平偏移 X", -800, 800, 2,
                () => Config.ScreenOffsetX, v => Config.ScreenOffsetX = v, v => v.ToString("0"));

            AddSlider("垂直偏移 Y", -40, 300, 1,
                () => Config.ScreenOffsetY, v => Config.ScreenOffsetY = v, v => v.ToString("0"));

            AddSlider("玻璃模糊强度", 0, 1, 0.01,
                () => Config.GlassBlurStrength, v => Config.GlassBlurStrength = v, v => v.ToString("0.00"));

            AddSlider("圆角半径", 0, 60, 1,
                () => Config.CornerRadius, v => Config.CornerRadius = v, v => v.ToString("0"));

            AddSlider("动画时长", 0, 800, 20,
                () => Config.AnimationDurationMs, v => Config.AnimationDurationMs = (int)Math.Round(v), v => v.ToString("0") + " ms");

            AddSlider("通知停留", 1000, 15000, 500,
                () => Config.NotificationDurationMs, v => Config.NotificationDurationMs = (int)Math.Round(v), v => v.ToString("0") + " ms");
        }

        private void AddSlider(
            string label,
            double min,
            double max,
            double step,
            Func<double> read,
            Action<double> write,
            Func<double, string> format)
        {
            var title = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = TitleBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var valueText = new TextBlock
            {
                FontSize = 11,
                Foreground = ValueBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);
            header.Children.Add(valueText);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                SmallChange = step,
                LargeChange = step * 4,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };

            slider.ValueChanged += (_, e) =>
            {
                valueText.Text = format(e.NewValue);

                if (_loading)
                {
                    return;
                }

                write(e.NewValue);
            };

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(slider);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 8, 11, 4),
                Margin = new Thickness(0, 0, 12, 10),
                Child = body,
            };

            SliderHost.Children.Add(card);

            _refreshers.Add(() =>
            {
                slider.Value = Math.Clamp(read(), min, max);
                valueText.Text = format(slider.Value);
            });
        }

        #endregion

        private void LoadFromConfig()
        {
            foreach (Action refresh in _refreshers)
            {
                refresh();
            }

            BlacklistBox.Text = string.Join(Environment.NewLine, Config.NotificationBlacklist);
            LyricsCheck.IsChecked = Config.LyricsEnabled;
        }

        private void OnBlacklistChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            List<string> names = BlacklistBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            if (!NamesEqual(names, Config.NotificationBlacklist))
            {
                Config.NotificationBlacklist = names;
            }
        }

        private void OnLyricsChanged(object sender, RoutedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            Config.LyricsEnabled = LyricsCheck.IsChecked == true;
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            _loading = true;
            Config.ResetToDefaults();
            LoadFromConfig();
            _loading = false;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static bool NamesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _service.SaveNow();
            base.OnClosed(e);
        }
    }
}
