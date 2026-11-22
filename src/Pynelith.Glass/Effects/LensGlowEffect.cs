using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// Siri 液态玻璃透镜的 ShaderEffect。
    ///
    /// 与 <see cref="LiquidGlassEffect"/> 的区别：
    /// 1. 形状是**椭圆**而非圆角矩形；
    /// 2. 多了一组「光带」参数（<see cref="Band"/>），中心横向色散光带
    ///    可按状态（收音 / 生成 / 空闲 / 收拢）连续变化；
    /// 3. 多了一个「透镜展开度」（<see cref="Reveal"/>），透镜从胶囊里长出来时由透明渐实。
    /// </summary>
    public sealed class LensGlowEffect : ShaderEffect
    {
        private static readonly PixelShader SharedShader = LoadShader();

        #region 着色器常量

        private static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(nameof(Resolution), typeof(Point), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point(240, 160), PixelShaderConstantCallback(0)));

        private static readonly DependencyProperty CenterProperty =
            DependencyProperty.Register(nameof(Center), typeof(Point), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point(120, 80), PixelShaderConstantCallback(1)));

        private static readonly DependencyProperty ShapeProperty =
            DependencyProperty.Register(nameof(Shape), typeof(Point4D), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point4D(26, 30, 1.45, 0), PixelShaderConstantCallback(2)));

        private static readonly DependencyProperty OpticsProperty =
            DependencyProperty.Register(nameof(Optics), typeof(Point4D), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point4D(0.06, 0, 0.35, 1), PixelShaderConstantCallback(3)));

        private static readonly DependencyProperty TintTopProperty =
            DependencyProperty.Register(nameof(TintTop), typeof(Color), typeof(LensGlowEffect),
                new UIPropertyMetadata(Color.FromRgb(4, 5, 7), PixelShaderConstantCallback(4)));

        private static readonly DependencyProperty TintBottomProperty =
            DependencyProperty.Register(nameof(TintBottom), typeof(Color), typeof(LensGlowEffect),
                new UIPropertyMetadata(Color.FromRgb(18, 22, 30), PixelShaderConstantCallback(5)));

        private static readonly DependencyProperty BackdropSizeProperty =
            DependencyProperty.Register(nameof(BackdropSize), typeof(Point), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point(480, 320), PixelShaderConstantCallback(6)));

        private static readonly DependencyProperty BackdropOffsetProperty =
            DependencyProperty.Register(nameof(BackdropOffset), typeof(Point), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point(120, 120), PixelShaderConstantCallback(7)));

        private static readonly DependencyProperty CompositeProperty =
            DependencyProperty.Register(nameof(Composite), typeof(Point4D), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point4D(0, 1, 0.6, 0), PixelShaderConstantCallback(8)));

        private static readonly DependencyProperty BandProperty =
            DependencyProperty.Register(nameof(Band), typeof(Point4D), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point4D(0.62, 0.85, 1, 1), PixelShaderConstantCallback(9)));

        private static readonly DependencyProperty HorizonProperty =
            DependencyProperty.Register(nameof(Horizon), typeof(Point4D), typeof(LensGlowEffect),
                new UIPropertyMetadata(new Point4D(0.5, 0.18, 0, 0), PixelShaderConstantCallback(10)));

        #endregion

        #region 语义化参数

        /// <summary>边缘折射带宽度（DIP），越大折射范围越宽。</summary>
        public double BezelWidth { get; set; } = 26;

        /// <summary>玻璃厚度（DIP），决定折射位移量。</summary>
        public double Thickness { get; set; } = 30;

        /// <summary>折射率，默认 1.45。</summary>
        public double GlassIor { get; set; } = 1.45;

        /// <summary>RGB 色散。</summary>
        public double Dispersion { get; set; } = 0.06;

        /// <summary>光源方位角（弧度）。</summary>
        public double LightAngle { get; set; }

        /// <summary>染色强度 0..1。</summary>
        public double TintAmount { get; set; } = 0.35;

        /// <summary>表面整体不透明度 0..1。</summary>
        public double SurfaceOpacity { get; set; } = 1.0;

        /// <summary>
        /// 内部染色权重 0..1。0 = 内部由采样背景完全不透明覆盖；
        /// 1 = 内部只输出染色 alpha，真实桌面由窗口自身透出（录屏兼容模式）。
        /// </summary>
        public double InteriorTintWeight { get; set; }

        /// <summary>透镜展开度 0..1：0 = 完全透明（尚未展开），1 = 完全显示。</summary>
        public double Reveal { get; set; } = 1;

        /// <summary>光带总强度 0..1（由状态驱动：脉动 / 流动 / 呼吸 / 收拢）。</summary>
        public double BandIntensity { get; set; } = 0.6;

        /// <summary>光带收拢度 0..1：1 = 完全收拢到中心并变暗（关闭动画）。</summary>
        public double BandCollapse { get; set; }

        /// <summary>光带半宽 0..1（相对椭圆半宽）。1 = 横贯整个椭圆。</summary>
        public double BandHalfWidth { get; set; } = 1.0;

        /// <summary>光带核心亮度 0..1：越大中心越白。</summary>
        public double BandCoreBrightness { get; set; } = 0.85;

        /// <summary>黑白分界位置 0..1（0 = 顶端，1 = 底端）。</summary>
        public double HorizonSplit { get; set; } = 0.5;

        /// <summary>分界过渡宽度 0..1（占玻璃高度的比例）。</summary>
        public double HorizonSoftness { get; set; } = 0.18;

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
            RegisterPixelShaderSamplerProperty("Input", typeof(LensGlowEffect), 0);

        #endregion

        private Point Resolution { get => (Point)GetValue(ResolutionProperty); set => SetValue(ResolutionProperty, value); }
        private Point Center { get => (Point)GetValue(CenterProperty); set => SetValue(CenterProperty, value); }
        private Point4D Shape { get => (Point4D)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
        private Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
        private Point4D Composite { get => (Point4D)GetValue(CompositeProperty); set => SetValue(CompositeProperty, value); }
        private Point4D Band { get => (Point4D)GetValue(BandProperty); set => SetValue(BandProperty, value); }
        private Point4D Horizon { get => (Point4D)GetValue(HorizonProperty); set => SetValue(HorizonProperty, value); }

        public LensGlowEffect()
        {
            PixelShader = SharedShader;

            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ResolutionProperty);
            UpdateShaderValue(CenterProperty);
            UpdateShaderValue(ShapeProperty);
            UpdateShaderValue(OpticsProperty);
            UpdateShaderValue(TintTopProperty);
            UpdateShaderValue(TintBottomProperty);
            UpdateShaderValue(BackdropSizeProperty);
            UpdateShaderValue(BackdropOffsetProperty);
            UpdateShaderValue(CompositeProperty);
            UpdateShaderValue(BandProperty);
            UpdateShaderValue(HorizonProperty);

            PushUniforms(240, 160, 1.0);
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
            Shape = new Point4D(BezelWidth * scale, Thickness * scale, GlassIor, 0);
            Optics = new Point4D(Dispersion, LightAngle, TintAmount,
                Math.Clamp(SurfaceOpacity, 0, 1));
            Composite = new Point4D(
                Math.Clamp(InteriorTintWeight, 0, 1),
                Math.Clamp(Reveal, 0, 1),
                Math.Clamp(BandIntensity, 0, 1),
                Math.Clamp(BandCollapse, 0, 1));
            Band = new Point4D(
                Math.Clamp(BandHalfWidth, 0.01, 1),
                Math.Clamp(BandCoreBrightness, 0, 1),
                1,
                1);
            Horizon = new Point4D(
                Math.Clamp(HorizonSplit, 0, 1),
                Math.Clamp(HorizonSoftness, 0.001, 0.5),
                0,
                0);
        }

        private static PixelShader LoadShader()
        {
            return new PixelShader
            {
                UriSource = new Uri(
                    "pack://application:,,,/Pynelith.Glass;component/Effects/Assets/LensGlow.gshader",
                    UriKind.Absolute),
            };
        }
    }
}
