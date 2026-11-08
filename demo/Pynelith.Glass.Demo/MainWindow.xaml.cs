using System;
using System.Windows;
using System.Windows.Controls;
using Pynelith.Glass;
using Pynelith.Glass.Effects;

namespace Pynelith.Glass.Demo
{
    /// <summary>
    /// 演示窗口：模拟一个顶部"岛"形玻璃面板。
    /// 只调用 Pynelith.Glass 的公开接口，不触碰渲染内部。
    /// </summary>
    public partial class MainWindow : Window
    {
        private IGlassSurface? _surface;
        private LiquidGlassSurface? _liquid;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Closed += (_, _) =>
            {
                _surface?.Dispose();
                _liquid?.Dispose();
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 岛形窗口定位：工作区顶部居中
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + 16;

            // 前景液态玻璃着色器（折射/高光/边缘）
            _liquid = LiquidGlassSurface.Install(Island, new LiquidGlassSurface.LiquidGlassStyle
            {
                CornerRadius = 20,
                EdgeWidth = 12,
                Refraction = 0.7,
                Highlight = 0.4,
                Tint = System.Windows.Media.Color.FromArgb(0x28, 0x20, 0x20, 0x28),
                TintOpacity = 0.30,
            });

            // 背景玻璃层（默认 Auto，按系统能力自动选择实现）
            ApplyBackdrop(GlassBackdropKind.Auto, GlassSystemStyle.Acrylic, "Auto");
        }

        private void OnKindClick(object sender, RoutedEventArgs e)
        {
            var tag = (sender as Button)?.Tag as string;
            switch (tag)
            {
                case "Mica":
                    ApplyBackdrop(GlassBackdropKind.SystemBackdrop, GlassSystemStyle.Mica, "SystemBackdrop · Mica");
                    break;
                case "Acrylic":
                    ApplyBackdrop(GlassBackdropKind.SystemBackdrop, GlassSystemStyle.Acrylic, "SystemBackdrop · Acrylic");
                    break;
                case "HostBlur":
                    ApplyBackdrop(GlassBackdropKind.CompositionBlur, GlassSystemStyle.Acrylic, "CompositionBlur · 伴随窗口");
                    break;
                case "Legacy":
                    ApplyBackdrop(GlassBackdropKind.LegacyBlur, GlassSystemStyle.Acrylic, "LegacyBlur · DWM BlurBehind");
                    break;
                default:
                    ApplyBackdrop(GlassBackdropKind.Auto, GlassSystemStyle.Acrylic, "Auto");
                    break;
            }
        }

        private void ApplyBackdrop(GlassBackdropKind kind, GlassSystemStyle systemStyle, string label)
        {
            _surface?.Dispose();

            var caps = GlassRenderer.GetCapabilities();

            _surface = GlassRenderer.Attach(this, new GlassOptions
            {
                Kind = kind,
                SystemStyle = systemStyle,
                CornerRadius = 20f,
            });

            StatusText.Text = string.Format(
                "{0}  →  生效: {1}   (OS build {2}, Win11: {3})",
                label, _surface.ActiveKind, caps.OsBuild, caps.IsWindows11);
        }
    }
}
