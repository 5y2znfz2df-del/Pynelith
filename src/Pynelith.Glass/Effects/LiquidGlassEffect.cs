using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 真正的液态玻璃 ShaderEffect —— 1:1 移植自 liquid-glass-rs 的 LIQUID_SKSL：
    /// 背景**不模糊**，圆角矩形边缘按 bezel 曲率做真实折射（含色散），
    /// 叠加竖向渐变染色、边缘镜面高光与 1.8px 轮廓。
    /// </summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        private static readonly PixelShader SharedShader = LoadShader();

        #region 着色器常量

        private static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(nameof(Resolution), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(428, 248), PixelShaderConstantCallback(0)));

        private static readonly DependencyProperty CenterProperty =
            DependencyProperty.Register(nameof(Center), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(214, 124), PixelShaderConstantCallback(1)));

        private static readonly DependencyProperty ShapeProperty =
            DependencyProperty.Register(nameof(Shape), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(46, 26, 34, 1.42), PixelShaderConstantCallback(2)));

        private static readonly DependencyProperty OpticsProperty =
            DependencyProperty.Register(nameof(Optics), typeof(Point4D), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0.05, 0, 0.70, 0), PixelShaderConstantCallback(3)));

        private static readonly DependencyProperty TintTopProperty =
            DependencyProperty.Register(nameof(TintTop), typeof(Color), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(Color.FromRgb(5, 6, 8), PixelShaderConstantCallback(4)));

        private static readonly DependencyProperty TintBottomProperty =
            DependencyProperty.Register(nameof(TintBottom), typeof(Color), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(Color.FromRgb(24, 28, 36), PixelShaderConstantCallback(5)));

        private static readonly DependencyProperty BackdropSizeProperty =
            DependencyProperty.Register(nameof(BackdropSize), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(668, 488), PixelShaderConstantCallback(6)));

        private static readonly DependencyProperty BackdropOffsetProperty =
            DependencyProperty.Register(nameof(BackdropOffset), typeof(Point), typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point(120, 120), PixelShaderConstantCallback(7)));

        #endregion

        #region 语义化参数

        /// <summary>圆角半径（DIP）。</summary>
        public double CornerRadius { get; set; } = 46;

        /// <summary>边缘折射带宽度（DIP）。</summary>
        public double BezelWidth { get; set; } = 26;

        /// <summary>玻璃厚度（DIP），决定折射位移量。</summary>
        public double Thickness { get; set; } = 34;

        /// <summary>折射率，默认 1.42。</summary>
        public double GlassIor { get; set; } = 1.42;

        /// <summary>RGB 色散。</summary>
        public double Dispersion { get; set; } = 0.05;

        /// <summary>光源方位角（弧度），通常跟光标相对玻璃中心的方位。</summary>
        public double LightAngle { get; set; }

        /// <summary>染色强度，黑玻璃约 0.70。</summary>
        public double TintAmount { get; set; } = 0.70;

        /// <summary>渐变染色上端（近黑）。</summary>
        public Color TintTop
        {
            get => (Color)GetValue(TintTopProperty);
            set => SetValue(TintTopProperty, value);
        }

        /// <summary>渐变染色下端（偏蓝灰）。</summary>
        public Color TintBottom
        {
            get => (Color)GetValue(TintBottomProperty);
            set => SetValue(TintBottomProperty, value);
        }

        /// <summary>抓屏图像尺寸（物理像素）。</summary>
        public Point BackdropSize
        {
            get => (Point)GetValue(BackdropSizeProperty);
            set => SetValue(BackdropSizeProperty, value);
        }

        /// <summary>元素原点在抓屏图像中的偏移（物理像素）。</summary>
        public Point BackdropOffset
        {
            get => (Point)GetValue(BackdropOffsetProperty);
            set => SetValue(BackdropOffsetProperty, value);
        }

        /// <summary>抓屏背景（ImageBrush）。</summary>
        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);

        #endregion

        private Point Resolution { get => (Point)GetValue(ResolutionProperty); set => SetValue(ResolutionProperty, value); }
        private Point Center { get => (Point)GetValue(CenterProperty); set => SetValue(CenterProperty, value); }
        private Point4D Shape { get => (Point4D)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
        private Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }

        public LiquidGlassEffect()
        {
            PixelShader = SharedShader;

            // 常量 DP 的默认值不会自动送入着色器，必须显式初始化。
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ResolutionProperty);
            UpdateShaderValue(CenterProperty);
            UpdateShaderValue(ShapeProperty);
            UpdateShaderValue(OpticsProperty);
            UpdateShaderValue(TintTopProperty);
            UpdateShaderValue(TintBottomProperty);
            UpdateShaderValue(BackdropSizeProperty);
            UpdateShaderValue(BackdropOffsetProperty);

            PushUniforms(428, 248, 1.0);
        }

        /// <summary>按元素尺寸与 DPI 重算着色器常量（长度按 DPI 缩放）。</summary>
        public void UpdateLayout(double width, double height, double dpiScale)
        {
            PushUniforms(width, height, dpiScale <= 0 ? 1.0 : dpiScale);
        }

        private void PushUniforms(double width, double height, double scale)
        {
            double w = Math.Max(width, 1) * scale;
            double h = Math.Max(height, 1) * scale;

            Resolution = new Point(w, h);
            Center = new Point(w * 0.5, h * 0.5);
            Shape = new Point4D(CornerRadius * scale, BezelWidth * scale, Thickness * scale, GlassIor);
            Optics = new Point4D(Dispersion, LightAngle, TintAmount, 0);
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
