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
            // 内容尺寸变化时保持顶部居中
            SizeChanged += (_, _) => CenterTop();
            Closed += (_, _) =>
            {
                _surface?.Dispose();
                _liquid?.Dispose();
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // --remote：让窗口出现在远程桌面/串流/录屏画面里（不做截屏排除，停用屏幕采样）
            bool remote = Array.IndexOf(Environment.GetCommandLineArgs(), "--remote") >= 0;

            // 液态玻璃：屏幕背景实时采样 + 超椭圆圆角 + 真实折射 + 色散 + 镜面光
            _liquid = LiquidGlassSurface.Install(Island, new LiquidGlassSurface.LiquidGlassStyle
            {
                CornerRadius = 22,
                BezelWidth = 18,
                Thickness = 14,
                GlassIor = 1.45,
                DisplacementFactor = 1.1,
                Dispersion = 0.08,
                Tint = System.Windows.Media.Color.FromRgb(10, 11, 15),
                TintOpacity = 0.373,
                Legibility = 0.32,
                Highlight = 0.75,
                BackgroundBlur = 6,
                ExcludeFromCapture = !remote,
            });

            // 默认不启用系统背景层：背景完全由玻璃着色器采样绘制
            ApplyBackdrop(GlassBackdropKind.None, GlassSystemStyle.Acrylic,
                remote ? "Liquid Glass · 远程兼容(无折射)" : "Liquid Glass · 屏幕采样");

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
            CenterTop();
        }

        private void CenterTop()
        {
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
                    ApplyPreset(0.0, 0.0, 1.1, 0.08, "Liquid Glass · Clear");
                    break;
                case "Frosted":
                    ApplyPreset(0.55, 0.45, 1.1, 0.02, "Liquid Glass · Frosted");
                    break;
                case "Tinted":
                    ApplyPreset(0.30, 0.30, 1.3, 0.14, "Liquid Glass · Tinted");
                    break;
                default:
                    ApplyBackdrop(GlassBackdropKind.SystemBackdrop, GlassSystemStyle.Acrylic, "对照: SystemBackdrop · Acrylic");
                    break;
            }
        }

        /// <summary>切换玻璃浓度档位（着色强度 / 可读性补偿 / 折射位移 / 色散）。</summary>
        private void ApplyPreset(double tintOpacity, double legibility, double displacement, double dispersion, string label)
        {
            ApplyBackdrop(GlassBackdropKind.None, GlassSystemStyle.Acrylic, label);

            _liquid?.Update(effect =>
            {
                effect.TintOpacity = tintOpacity;
                effect.Legibility = legibility;
                effect.DisplacementFactor = displacement;
                effect.Dispersion = dispersion;
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
