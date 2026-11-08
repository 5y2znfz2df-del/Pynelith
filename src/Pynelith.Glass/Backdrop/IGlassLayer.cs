using System;
using System.Windows;
using System.Windows.Media;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// 内部背景层契约。所有方法仅在 UI 线程调用；
    /// 坐标一律为设备像素（由 GlassSurface 负责 DIP→设备 转换）。
    /// </summary>
    internal interface IGlassLayer : IDisposable
    {
        GlassBackdropKind Kind { get; }

        /// <summary>初始化层；返回 false 表示该系统/窗口条件下不可用，调用方应降级。</summary>
        bool TryInitialize(IntPtr ownerHwnd, GlassOptions options);

        /// <summary>owner 窗口在屏幕上的矩形（设备像素）发生变化时调用。</summary>
        void OnOwnerRectChanged(Rect ownerRectDevice, float cornerRadiusDevice);

        /// <summary>owner 窗口显示/隐藏（含最小化）时调用。</summary>
        void OnOwnerVisibilityChanged(bool visible);

        void SetBlurStrength(double strength);

        void SetTint(Color color, double opacity);
    }
}
