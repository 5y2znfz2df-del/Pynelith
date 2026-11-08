using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pynelith.Glass.Effects
{
    /// <summary>
    /// 把 LiquidGlassEffect 安装到任意 FrameworkElement 并自动同步尺寸。
    /// UI 模块用法：
    /// <code>
    /// var glassSurface = LiquidGlassSurface.Install(myIslandBorder, new LiquidGlassStyle { ... });
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
                CornerRadius = style.CornerRadius,
                EdgeWidth = style.EdgeWidth,
                Refraction = style.Refraction,
                Highlight = style.Highlight,
                Tint = style.Tint,
                TintOpacity = style.TintOpacity,
                Resolution = new Point(Math.Max(1, target.ActualWidth), Math.Max(1, target.ActualHeight)),
            };

            // 输入画刷：元素背景（未设置时给一个半透明底，保证效果可见）
            _effect.Input = GetBackground(target) ?? new SolidColorBrush(Color.FromArgb(0x50, 0x18, 0x18, 0x1C));

            target.Effect = _effect;
            target.SizeChanged += OnTargetSizeChanged;
        }

        /// <summary>从常见容器元素（Border/Panel/Control）读取背景画刷。</summary>
        private static Brush? GetBackground(FrameworkElement target) => target switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            Control control => control.Background,
            _ => null,
        };

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
