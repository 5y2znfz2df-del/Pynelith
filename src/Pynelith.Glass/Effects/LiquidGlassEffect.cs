using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 液态玻璃表面 ShaderEffect（GPU 像素着色，ps_3_0）。
    /// 对元素的 Brush 内容做圆角边缘折射、高光与着色；
    /// 与 GlassRenderer.Attach 的背景模糊层叠加使用。
    /// </summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        private static readonly PixelShader SharedShader = LoadShader();

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LiquidGlassEffect), 0);

        public static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(nameof(Resolution), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(200, 60), PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(16d, PixelShaderConstantCallback(1)));

        public static readonly DependencyProperty EdgeWidthProperty =
            DependencyProperty.Register(nameof(EdgeWidth), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(14d, PixelShaderConstantCallback(2)));

        public static readonly DependencyProperty RefractionProperty =
            DependencyProperty.Register(nameof(Refraction), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(0.6d, PixelShaderConstantCallback(3)));

        public static readonly DependencyProperty HighlightProperty =
            DependencyProperty.Register(nameof(Highlight), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(0.35d, PixelShaderConstantCallback(4)));

        public static readonly DependencyProperty TintProperty =
            DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(Color.FromRgb(0x2A, 0x2A, 0x30), PixelShaderConstantCallback(5)));

        public static readonly DependencyProperty TintOpacityProperty =
            DependencyProperty.Register(nameof(TintOpacity), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(0.45d, PixelShaderConstantCallback(6)));

        public LiquidGlassEffect()
        {
            PixelShader = SharedShader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ResolutionProperty);
            UpdateShaderValue(CornerRadiusProperty);
            UpdateShaderValue(EdgeWidthProperty);
            UpdateShaderValue(RefractionProperty);
            UpdateShaderValue(HighlightProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(TintOpacityProperty);
        }

        /// <summary>元素自身内容画刷（WPF 自动填充）。</summary>
        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        /// <summary>元素像素尺寸，需随布局更新（可用 LiquidGlassSurface.Install 自动同步）。</summary>
        public Point Resolution
        {
            get => (Point)GetValue(ResolutionProperty);
            set => SetValue(ResolutionProperty, value);
        }

        public double CornerRadius
        {
            get => (double)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public double EdgeWidth
        {
            get => (double)GetValue(EdgeWidthProperty);
            set => SetValue(EdgeWidthProperty, value);
        }

        public double Refraction
        {
            get => (double)GetValue(RefractionProperty);
            set => SetValue(RefractionProperty, value);
        }

        public double Highlight
        {
            get => (double)GetValue(HighlightProperty);
            set => SetValue(HighlightProperty, value);
        }

        public Color Tint
        {
            get => (Color)GetValue(TintProperty);
            set => SetValue(TintProperty, value);
        }

        public double TintOpacity
        {
            get => (double)GetValue(TintOpacityProperty);
            set => SetValue(TintOpacityProperty, value);
        }

        private static PixelShader LoadShader()
        {
            // 着色器以 WPF Resource 形式打包，通过 pack URI 加载（进程内共享单例）。
            return new PixelShader
            {
                UriSource = new Uri(
                    "pack://application:,,,/Pynelith.Glass;component/Effects/Assets/LiquidGlass.gshader",
                    UriKind.Absolute),
            };
        }
    }
}
