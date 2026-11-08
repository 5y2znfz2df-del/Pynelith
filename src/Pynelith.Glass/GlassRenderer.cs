using System;
using System.Windows;
using System.Windows.Media;
using Pynelith.Glass.Backdrop;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass
{
    /// <summary>
    /// 渲染模块对外门面。UI 模块（feature/island-ui）只需：
    /// <code>
    /// var glass = GlassRenderer.Attach(myWindow, new GlassOptions());
    /// </code>
    /// 不接触任何渲染内部。
    /// </summary>
    public static class GlassRenderer
    {
        /// <summary>查询当前系统玻璃能力。</summary>
        public static GlassCapabilities GetCapabilities()
        {
            var caps = new GlassCapabilities
            {
                OsBuild = OsInfo.Build,
                IsWindows11 = OsInfo.IsWindows11,
                SupportsSystemBackdrop = OsInfo.SupportsSystemBackdrop,
                SupportsCompositionBlur = OsInfo.SupportsCompositionBackdrop,
                SupportsLegacyBlur = true,
            };

            caps.PreferredKind = caps.SupportsSystemBackdrop
                ? GlassBackdropKind.SystemBackdrop
                : caps.SupportsCompositionBlur
                    ? GlassBackdropKind.CompositionBlur
                    : GlassBackdropKind.LegacyBlur;

            return caps;
        }

        /// <summary>
        /// 为窗口附加玻璃背景。要求窗口 Background 为透明或半透明，
        /// 否则背景层会被 WPF 自身内容遮挡（这是 UI 侧的样式职责，模块不越权修改）。
        /// 返回的表面 Dispose 时会同时解除对窗口事件的订阅。
        /// </summary>
        public static IGlassSurface Attach(Window window, GlassOptions? options = null)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            return new SurfaceBridge(window, options ?? new GlassOptions());
        }

        /// <summary>
        /// 桥接层：把 WPF 可见性/最小化状态同步给背景层，
        /// 并在 Dispose 时解除事件订阅，避免窗口长期存活时泄漏。
        /// </summary>
        private sealed class SurfaceBridge : IGlassSurface
        {
            private readonly Window _window;
            private readonly GlassSurface _surface;
            private readonly DependencyPropertyChangedEventHandler _visibilityChanged;
            private readonly EventHandler _stateChanged;
            private bool _disposed;

            internal SurfaceBridge(Window window, GlassOptions options)
            {
                _window = window;
                _surface = new GlassSurface(window, options);

                _visibilityChanged = (_, _) => NotifyActive();
                _stateChanged = (_, _) => NotifyActive();

                window.IsVisibleChanged += _visibilityChanged;
                window.StateChanged += _stateChanged;
            }

            private void NotifyActive()
            {
                _surface.NotifyVisibility(_window.IsVisible && _window.WindowState != WindowState.Minimized);
            }

            public GlassBackdropKind ActiveKind => _surface.ActiveKind;

            public void UpdateShape(Rect shapeInDip, float cornerRadiusInDip) => _surface.UpdateShape(shapeInDip, cornerRadiusInDip);

            public void SetBlurStrength(double strength) => _surface.SetBlurStrength(strength);

            public void SetTint(Color color, double opacity) => _surface.SetTint(color, opacity);

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _window.IsVisibleChanged -= _visibilityChanged;
                _window.StateChanged -= _stateChanged;
                _surface.Dispose();
            }
        }
    }
}
