using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pynelith.Glass;
using Pynelith.Glass.Effects;

namespace Pynelith.Glass.Demo
{
    /// <summary>
    /// 演示窗口：苹果风液态玻璃岛。只调用公开接口，不触碰渲染内部。
    /// </summary>
    public partial class MainWindow : Window
    {
        private IGlassSurface? _surface;
        private LiquidGlassSurface? _liquid;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            // SizeToContent 在首次布局后才有实际宽度，定位放到 ContentRendered
            ContentRendered += OnContentRendered;
            Closed += (_, _) =>
            {
                _surface?.Dispose();
                _liquid?.Dispose();
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 液态玻璃：屏幕背景实时采样 + 边缘折射 + 镜面亮边
            _liquid = LiquidGlassSurface.Install(Island, new LiquidGlassSurface.LiquidGlassStyle
            {
                CornerRadius = 22,
                EdgeBand = 20,
                Refraction = 0.95,
                BlurRadius = 4.0,
                Saturation = 1.7,
                Brightness = 1.08,
                Tint = System.Windows.Media.Color.FromRgb(0xF4, 0xF8, 0xFF),
                TintOpacity = 0.05,
                Highlight = 0.95,
            });

            // 默认不启用系统背景层：背景完全由玻璃着色器采样绘制
            ApplyBackdrop(GlassBackdropKind.None, GlassSystemStyle.Acrylic, "Liquid Glass · 屏幕采样");

            // 采样是异步启动的，稍后再回报实际状态
            var report = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800),
            };
            report.Tick += (_, _) =>
            {
                report.Stop();
                StatusText.Text += _liquid?.IsScreenBackdropActive == true
                    ? "   ·   采样: 开"
                    : "   ·   采样: 关(降级)";
            };
            report.Start();
        }

        /// <summary>岛形窗口：工作区顶部居中。</summary>
        private void OnContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OnContentRendered;

            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Top + 16;
        }

        private void OnIslandDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OnKindClick(object sender, RoutedEventArgs e)
        {
            var tag = (sender as Button)?.Tag as string;
            switch (tag)
            {
                case "Clear":
                    ApplyPreset(0.02, 1.50, 1.10, 3.0, "Liquid Glass · Clear");
                    break;
                case "Frosted":
                    ApplyPreset(0.16, 1.20, 1.18, 10.0, "Liquid Glass · Frosted");
                    break;
                case "Tinted":
                    ApplyPreset(0.10, 1.65, 1.08, 5.0, "Liquid Glass · Tinted");
                    break;
                default:
                    ApplyBackdrop(GlassBackdropKind.SystemBackdrop, GlassSystemStyle.Acrylic, "对照: SystemBackdrop · Acrylic");
                    break;
            }
        }

        /// <summary>切换液态玻璃浓度档位（苹果玻璃的三种常用通透度）。</summary>
        private void ApplyPreset(double tintOpacity, double saturation, double brightness, double blur, string label)
        {
            ApplyBackdrop(GlassBackdropKind.None, GlassSystemStyle.Acrylic, label);

            _liquid?.Update(effect =>
            {
                effect.TintOpacity = tintOpacity;
                effect.Saturation = saturation;
                effect.Brightness = brightness;
                effect.BlurRadius = blur;
            });
        }

        private void ApplyBackdrop(GlassBackdropKind kind, GlassSystemStyle systemStyle, string label)
        {
            _surface?.Dispose();
            _surface = null;

            var caps = GlassRenderer.GetCapabilities();

            if (kind != GlassBackdropKind.None)
            {
                _surface = GlassRenderer.Attach(this, new GlassOptions
                {
                    Kind = kind,
                    SystemStyle = systemStyle,
                    CornerRadius = 22f,
                });
            }

            StatusText.Text = string.Format(
                "{0}   ·   生效: {1}   (build {2})",
                label,
                _surface?.ActiveKind.ToString() ?? "None",
                caps.OsBuild);
        }
    }
}
