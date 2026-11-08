using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 苹果风液态玻璃 ShaderEffect（GPU 像素着色，ps_3_0）。
    /// 输入为背景采样（屏幕截图），输出带圆角裁剪的玻璃表面：
    /// 轻模糊 + 高饱和背景、边缘透镜折射、镜面亮边、内侧厚度阴影。
    /// </summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        private static readonly PixelShader SharedShader = LoadShader();

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LiquidGlassEffect), 0);

        public static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(nameof(Resolution), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(400, 120), PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(20d, PixelShaderConstantCallback(1)));

        public static readonly DependencyProperty EdgeBandProperty =
            DependencyProperty.Register(nameof(EdgeBand), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(18d, PixelShaderConstantCallback(2)));

        public static readonly DependencyProperty RefractionProperty =
            DependencyProperty.Register(nameof(Refraction), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(0.85d, PixelShaderConstantCallback(3)));

        public static readonly DependencyProperty BlurRadiusProperty =
            DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(3.5d, PixelShaderConstantCallback(4)));

        public static readonly DependencyProperty SaturationProperty =
            DependencyProperty.Register(nameof(Saturation), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(1.55d, PixelShaderConstantCallback(5)));

        public static readonly DependencyProperty BrightnessProperty =
            DependencyProperty.Register(nameof(Brightness), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(1.06d, PixelShaderConstantCallback(6)));

        public static readonly DependencyProperty TintProperty =
            DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFF), PixelShaderConstantCallback(7)));

        public static readonly DependencyProperty TintOpacityProperty =
            DependencyProperty.Register(nameof(TintOpacity), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(0.06d, PixelShaderConstantCallback(8)));

        public static readonly DependencyProperty HighlightProperty =
            DependencyProperty.Register(nameof(Highlight), typeof(double), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(0.85d, PixelShaderConstantCallback(9)));

        public LiquidGlassEffect()
        {
            PixelShader = SharedShader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ResolutionProperty);
            UpdateShaderValue(CornerRadiusProperty);
            UpdateShaderValue(EdgeBandProperty);
            UpdateShaderValue(RefractionProperty);
            UpdateShaderValue(BlurRadiusProperty);
            UpdateShaderValue(SaturationProperty);
            UpdateShaderValue(BrightnessProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(TintOpacityProperty);
            UpdateShaderValue(HighlightProperty);
        }

        /// <summary>背景采样输入（ImageBrush 屏幕截图；也可用 Effect.ImplicitInput）。</summary>
        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        /// <summary>元素像素尺寸，需随布局更新（LiquidGlassSurface 会自动同步）。</summary>
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

        /// <summary>边缘折射带宽度（像素）。</summary>
        public double EdgeBand
        {
            get => (double)GetValue(EdgeBandProperty);
            set => SetValue(EdgeBandProperty, value);
        }

        public double Refraction
        {
            get => (double)GetValue(RefractionProperty);
            set => SetValue(RefractionProperty, value);
        }

        public double BlurRadius
        {
            get => (double)GetValue(BlurRadiusProperty);
            set => SetValue(BlurRadiusProperty, value);
        }

        public double Saturation
        {
            get => (double)GetValue(SaturationProperty);
            set => SetValue(SaturationProperty, value);
        }

        public double Brightness
        {
            get => (double)GetValue(BrightnessProperty);
            set => SetValue(BrightnessProperty, value);
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

        public double Highlight
        {
            get => (double)GetValue(HighlightProperty);
            set => SetValue(HighlightProperty, value);
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
