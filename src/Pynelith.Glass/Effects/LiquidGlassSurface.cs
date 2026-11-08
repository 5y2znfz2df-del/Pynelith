using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 把 LiquidGlassEffect 安装到任意 FrameworkElement 并自动同步尺寸。
    /// UI 模块用法：
    /// <code>
    /// var glassSurface = LiquidGlassSurface.Install(myIslandBorder, new LiquidGlassSurface.LiquidGlassStyle { ... });
    /// </code>
    /// </summary>
    public sealed class LiquidGlassSurface : IDisposable
    {
        private readonly FrameworkElement _target;
        private readonly LiquidGlassEffect _effect;

        public sealed class LiquidGlassStyle
        {
            public double CornerRadius { get; set; } = 16;
            public double EdgeWidth { get; set; } = 14;
            public double Refraction { get; set; } = 0.6;
            public double Highlight { get; set; } = 0.35;
            public Color Tint { get; set; } = Color.FromRgb(0x2A, 0x2A, 0x30);
            public double TintOpacity { get; set; } = 0.45;
        }

        private LiquidGlassSurface(FrameworkElement target, LiquidGlassStyle style)
        {
            _target = target;
            _effect = new LiquidGlassEffect
            {
                // WPF 采样器只接受 Effect.ImplicitInput 或 BitmapCacheBrush/VisualBrush/ImageBrush，
                // 传 SolidColorBrush 会抛 ArgumentException，因此固定采样元素自身已渲染内容。
                Input = ShaderEffect.ImplicitInput,
                CornerRadius = style.CornerRadius,
                EdgeWidth = style.EdgeWidth,
                Refraction = style.Refraction,
                Highlight = style.Highlight,
                Tint = style.Tint,
                TintOpacity = style.TintOpacity,
                Resolution = new Point(Math.Max(1, target.ActualWidth), Math.Max(1, target.ActualHeight)),
            };

            target.Effect = _effect;
            target.SizeChanged += OnTargetSizeChanged;
        }

        public static LiquidGlassSurface Install(FrameworkElement target, LiquidGlassStyle? style = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return new LiquidGlassSurface(target, style ?? new LiquidGlassStyle());
        }

        /// <summary>调节效果参数（线程安全要求：UI 线程调用）。</summary>
        public void Update(Action<LiquidGlassEffect> configure)
        {
            configure?.Invoke(_effect);
        }

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _effect.Resolution = new Point(
                Math.Max(1, _target.ActualWidth),
                Math.Max(1, _target.ActualHeight));
        }

        public void Dispose()
        {
            _target.SizeChanged -= OnTargetSizeChanged;
            if (ReferenceEquals(_target.Effect, _effect))
            {
                _target.Effect = null;
            }
        }
    }
}
