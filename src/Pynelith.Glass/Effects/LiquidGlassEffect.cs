using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 苹果风液态玻璃 ShaderEffect —— 忠实移植 WinIsland 的 liquid.sksl：
    /// 超椭圆圆角 SDF、边缘斜率法线、真实折射、RGB 色散、镜面光与投影。
    /// 参数默认值取自 WinIsland 的 LiquidMaterial::island(scale)。
    /// </summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        private const float ContinuousCornerExponent = 2.5f;
        private const float CircularApexInset = 1f - 0.70710678f;
        private const float MaxCornerSmoothing = 3.3333333f;

        private static readonly PixelShader SharedShader = LoadShader();

        #region 常量寄存器（打包传参）

        private static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(nameof(Resolution), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(400, 120), PixelShaderConstantCallback(0)));

        private static readonly DependencyProperty CornerProperty =
            DependencyProperty.Register(nameof(Corner), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 0, 10, 2.5), PixelShaderConstantCallback(1)));

        private static readonly DependencyProperty BezelProperty =
            DependencyProperty.Register(nameof(Bezel), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(18, 14, 1.45, 1.1), PixelShaderConstantCallback(2)));

        private static readonly DependencyProperty OpticsProperty =
            DependencyProperty.Register(nameof(Optics), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0.08, 1, 1, 0.32), PixelShaderConstantCallback(3)));

        private static readonly DependencyProperty TintProperty =
            DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(Color.FromArgb(95, 10, 11, 15), PixelShaderConstantCallback(4)));

        private static readonly DependencyProperty LightDirectionProperty =
            DependencyProperty.Register(nameof(LightDirection), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(-0.70710678, -0.70710678), PixelShaderConstantCallback(5)));

        private static readonly DependencyProperty SpecularProperty =
            DependencyProperty.Register(nameof(Specular), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0.75, 0.38, 0.8, 0.9), PixelShaderConstantCallback(6)));

        private static readonly DependencyProperty SpecularShapeProperty =
            DependencyProperty.Register(nameof(SpecularShape), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(1.5, 3), PixelShaderConstantCallback(7)));

        private static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0.65, 0.25, 0.2, 0), PixelShaderConstantCallback(8)));

        private static readonly DependencyProperty ShadowProperty =
            DependencyProperty.Register(nameof(Shadow), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 2, 7, 0), PixelShaderConstantCallback(9)));

        private static readonly DependencyProperty ExtraProperty =
            DependencyProperty.Register(nameof(Extra), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 0.22, 0.6, 0.7), PixelShaderConstantCallback(10)));

        private static readonly DependencyProperty Extra2Property =
            DependencyProperty.Register(nameof(Extra2), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(6, 0), PixelShaderConstantCallback(11)));

        #endregion

        #region 语义化参数（WinIsland LiquidMaterial）

        /// <summary>玻璃圆角半径（DIP，连续圆角）。</summary>
        public double CornerRadius { get; set; } = 22;

        /// <summary>边缘折射带宽度（DIP），决定"玻璃厚度"的视觉范围。</summary>
        public double BezelWidth { get; set; } = 18;

        /// <summary>玻璃厚度（DIP）。</summary>
        public double Thickness { get; set; } = 14;

        /// <summary>玻璃折射率，默认 1.45。</summary>
        public double GlassIor { get; set; } = 1.45;

        /// <summary>折射位移缩放。</summary>
        public double DisplacementFactor { get; set; } = 1.1;

        /// <summary>RGB 色散强度 0..0.5。</summary>
        public double Dispersion { get; set; } = 0.08;

        /// <summary>着色 alpha/强度（0 = 全透明玻璃）。</summary>
        public double TintOpacity { get; set; } = 0.373;

        /// <summary>可读性补偿：亮背景自动加着色，保证文字可读。</summary>
        public double Legibility { get; set; } = 0.32;

        /// <summary>镜面光强度。</summary>
        public double SpecularStrength { get; set; } = 0.75;

        /// <summary>反向补光强度。</summary>
        public double OppositeSpecularStrength { get; set; } = 0.38;

        /// <summary>镜面锐利度。</summary>
        public double SpecularSharpness { get; set; } = 3;

        /// <summary>镜面衰减。</summary>
        public double SpecularFalloff { get; set; } = 0.9;

        /// <summary>镜面不透明度。</summary>
        public double SpecularOpacity { get; set; } = 0.8;

        /// <summary>镜面带宽度（DIP）。</summary>
        public double SpecularWidth { get; set; } = 1.5;

        /// <summary>外描边宽度（DIP，0 关闭）。</summary>
        public double StrokeWidth { get; set; } = 0.65;

        /// <summary>外描边不透明度。</summary>
        public double StrokeOpacity { get; set; } = 0.25;

        /// <summary>投影偏移（DIP）。</summary>
        public Point ShadowOffset { get; set; } = new Point(0, 2);

        /// <summary>投影半径（DIP）。</summary>
        public double ShadowRadius { get; set; } = 7;

        /// <summary>投影扩散（DIP，负值内收）。</summary>
        public double ShadowSpread { get; set; } = -1;

        /// <summary>投影不透明度。</summary>
        public double ShadowAlpha { get; set; } = 0.22;

        /// <summary>材质进度 0..1（1 为完全成形）。</summary>
        public double Progress { get; set; } = 1;

        /// <summary>整体不透明度 0..1。</summary>
        public double Opacity { get; set; } = 1;

        /// <summary>背景预模糊半径（物理像素）。</summary>
        public double BackgroundBlur { get; set; } = 6;

        /// <summary>DPI 缩放，长度参数会自动乘以它。</summary>
        public double PixelScale { get; set; } = 1;

        /// <summary>玻璃着色颜色（RGB），alpha 由 TintOpacity 控制。</summary>
        public Color Tint
        {
            get => (Color)GetValue(TintProperty);
            set => SetValue(TintProperty, value);
        }

        /// <summary>光照方向（会归一化），默认左上。</summary>
        public Point LightDirection
        {
            get => (Point)GetValue(LightDirectionProperty);
            set => SetValue(LightDirectionProperty, value);
        }

        /// <summary>后端采样画刷（屏幕背景截图）。</summary>
        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);

        #endregion

        // 打包用的内部常量
        private Point Resolution { get => (Point)GetValue(ResolutionProperty); set => SetValue(ResolutionProperty, value); }
        private Point4D Corner { get => (Point4D)GetValue(CornerProperty); set => SetValue(CornerProperty, value); }
        private Point4D Bezel { get => (Point4D)GetValue(BezelProperty); set => SetValue(BezelProperty, value); }
        private Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
        private Point4D Specular { get => (Point4D)GetValue(SpecularProperty); set => SetValue(SpecularProperty, value); }
        private Point SpecularShape { get => (Point)GetValue(SpecularShapeProperty); set => SetValue(SpecularShapeProperty, value); }
        private Point4D Stroke { get => (Point4D)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        private Point4D Shadow { get => (Point4D)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }
        private Point4D Extra { get => (Point4D)GetValue(ExtraProperty); set => SetValue(ExtraProperty, value); }
        private Point Extra2 { get => (Point)GetValue(Extra2Property); set => SetValue(Extra2Property, value); }

        public LiquidGlassEffect()
        {
            PixelShader = SharedShader;

            // ShaderEffect 的常量 DP 默认值不会自动送入着色器，
            // 必须显式初始化，否则未变更过的寄存器在 GPU 侧为 0。
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ResolutionProperty);
            UpdateShaderValue(CornerProperty);
            UpdateShaderValue(BezelProperty);
            UpdateShaderValue(OpticsProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(LightDirectionProperty);
            UpdateShaderValue(SpecularProperty);
            UpdateShaderValue(SpecularShapeProperty);
            UpdateShaderValue(StrokeProperty);
            UpdateShaderValue(ShadowProperty);
            UpdateShaderValue(ExtraProperty);
            UpdateShaderValue(Extra2Property);

            PushUniforms(400, 120);
        }

        /// <summary>按元素尺寸与 DPI 重新计算全部着色器常量。</summary>
        public void UpdateLayout(double width, double height, double dpiScale)
        {
            PixelScale = dpiScale <= 0 ? 1 : dpiScale;
            PushUniforms(width, height);
        }

        private void PushUniforms(double width, double height)
        {
            double w = Math.Max(width, 1) * PixelScale;
            double h = Math.Max(height, 1) * PixelScale;
            double s = PixelScale;

            Resolution = new Point(w, h);

            double minHalf = Math.Max(Math.Min(w, h) * 0.5, 1.0);
            var (extent, exponent) = MapCorner(w, h, CornerRadius * s);
            Corner = new Point4D(extent, exponent, 0, 0);

            double bezel = Math.Max(Math.Min(BezelWidth * s, minHalf * 0.7), 1.0);
            double thickness = Thickness * s * bezel / Math.Max(BezelWidth * s, 1.0);
            Bezel = new Point4D(bezel, thickness, GlassIor, DisplacementFactor);

            Optics = new Point4D(
                Math.Clamp(Dispersion, 0, 0.5),
                Math.Clamp(Progress, 0, 1),
                Math.Clamp(Opacity, 0, 1),
                Math.Clamp(Legibility, 0, 1));

            Tint = Color.FromArgb(
                (byte)Math.Clamp(Math.Round(TintOpacity * 255), 0, 255),
                Tint.R, Tint.G, Tint.B);

            var light = Normalize(LightDirection);
            LightDirection = light;

            Specular = new Point4D(SpecularStrength, OppositeSpecularStrength, SpecularOpacity, SpecularFalloff);
            SpecularShape = new Point(Math.Max(SpecularWidth * s, 0.25), SpecularSharpness);
            Stroke = new Point4D(StrokeWidth * s, StrokeOpacity, 0.2 * s, 0);
            Shadow = new Point4D(ShadowOffset.X * s, ShadowOffset.Y * s, Math.Max(ShadowRadius * s, 0), 0);
            Extra = new Point4D(ShadowSpread * s, ShadowAlpha, 0.6, 0.7);
            Extra2 = new Point(Math.Max(BackgroundBlur, 0), 0);
        }

        /// <summary>连续圆角半径 → 超椭圆（squircle）参数，与 WinIsland 的 corner() 一致。</summary>
        private static (double extent, double exponent) MapCorner(double width, double height, double radius)
        {
            double limit = Math.Max(Math.Min(width, height) * 0.5, 0.5);
            double r = Math.Clamp(radius, 0.5, limit);
            double extent = r * CircularApexInset / (1.0 - Math.Pow(2.0, -1.0 / ContinuousCornerExponent));

            if (extent <= limit)
            {
                return (extent, ContinuousCornerExponent);
            }

            double exponent = 1.0 / -Math.Log(1.0 - r * CircularApexInset / limit, 2.0);
            return (limit, Math.Clamp(exponent, 2.0, 2.0 + MaxCornerSmoothing));
        }

        private static Point Normalize(Point p)
        {
            double len = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            return len < 1e-4 ? new Point(-0.70710678, -0.70710678) : new Point(p.X / len, p.Y / len);
        }

        private static PixelShader LoadShader()
        {
            return new PixelShader
            {
                UriSource = new Uri(
                    "pack://application:,,,/Pynelith.Glass;component/Effects/Assets/LiquidGlass.gshader",
                    UriKind.Absolute),
            };
        }
    }
}
