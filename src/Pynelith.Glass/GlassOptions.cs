using System;
using System.Windows.Media;

namespace Pynelith.Glass
{
    /// <summary>背景模糊实现方式。</summary>
    public enum GlassBackdropKind
    {
        /// <summary>按系统能力自动选择最优实现（推荐）。</summary>
        Auto,

        /// <summary>不启用背景层。</summary>
        None,

        /// <summary>Win11 22H2+：DWM 系统背景（Mica/Acrylic/Tabbed），零额外 GPU 开销。</summary>
        SystemBackdrop,

        /// <summary>伴随窗口 + DWM 桌面背景模糊 + 形状裁剪（WinIsland glass 同架构，Win10/11 通用）。</summary>
        CompositionBlur,

        /// <summary>Win10 兼容层：DwmEnableBlurBehindWindow。</summary>
        LegacyBlur,
    }

    /// <summary>Win11 系统背景样式。</summary>
    public enum GlassSystemStyle
    {
        Mica,
        Acrylic,
        Tabbed,
    }

    /// <summary>玻璃窗口配置。</summary>
    public sealed class GlassOptions
    {
        /// <summary>实现方式选择，默认 Auto。</summary>
        public GlassBackdropKind Kind { get; set; } = GlassBackdropKind.Auto;

        /// <summary>SystemBackdrop 模式下的样式。</summary>
        public GlassSystemStyle SystemStyle { get; set; } = GlassSystemStyle.Acrylic;

        /// <summary>圆角半径（DIP，逻辑像素）。</summary>
        public float CornerRadius { get; set; } = 16f;

        /// <summary>模糊强度 0..1（仅 CompositionBlur 模式生效）。</summary>
        public double BlurStrength { get; set; } = 0.6;

        /// <summary>饱和度 0..1（仅 CompositionBlur 模式生效，模拟 Acrylic 去饱和）。</summary>
        public double Saturation { get; set; } = 0.55;

        /// <summary>着色层颜色。</summary>
        public Color TintColor { get; set; } = Color.FromRgb(0x20, 0x20, 0x24);

        /// <summary>着色层不透明度 0..1。</summary>
        public double TintOpacity { get; set; } = 0.55;

        /// <summary>浅色主题下的着色覆盖（可选，null 表示不区分主题）。</summary>
        public Color? LightThemeTint { get; set; }

        public GlassOptions Clone() => (GlassOptions)MemberwiseClone();
    }

    /// <summary>当前系统可用的玻璃能力。</summary>
    public sealed class GlassCapabilities
    {
        public int OsBuild { get; internal set; }
        public bool IsWindows11 { get; internal set; }

        /// <summary>支持 DWMWA_SYSTEMBACKDROP_TYPE（Win11 22H2+）。</summary>
        public bool SupportsSystemBackdrop { get; internal set; }

        /// <summary>支持伴随窗口背景模糊层（WS_EX_NOREDIRECTIONBITMAP，Win10+）。</summary>
        public bool SupportsCompositionBlur { get; internal set; }

        /// <summary>支持 DwmEnableBlurBehindWindow（Win7+，DWM 合成开启）。</summary>
        public bool SupportsLegacyBlur { get; internal set; }

        /// <summary>Auto 模式将选择的实现。</summary>
        public GlassBackdropKind PreferredKind { get; internal set; }
    }

    /// <summary>
    /// 已附加到窗口的玻璃表面。UI 模块只持有并调用它，不接触渲染内部。
    /// </summary>
    public interface IGlassSurface : IDisposable
    {
        /// <summary>实际生效的实现方式。</summary>
        GlassBackdropKind ActiveKind { get; }

        /// <summary>
        /// 更新玻璃形状（窗口移动/缩放/自定义圆角时由 UI 调用；
        /// 常规整窗玻璃无需调用，模块会自动跟随窗口）。
        /// </summary>
        /// <param name="shapeInDip">相对窗口的形状（DIP）。默认覆盖整个客户区。</param>
        /// <param name="cornerRadiusInDip">圆角半径（DIP）。</param>
        void UpdateShape(System.Windows.Rect shapeInDip, float cornerRadiusInDip);

        /// <summary>动态调整模糊强度 0..1（CompositionBlur 有效）。</summary>
        void SetBlurStrength(double strength);

        /// <summary>动态调整着色层。</summary>
        void SetTint(Color color, double opacity);
    }
}
