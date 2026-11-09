using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pynelith.Island.Configuration
{
    /// <summary>
    /// 灵动岛全部可配置项，直接序列化为 <c>%AppData%\Pynelith\config.json</c>。
    /// 属性写入即触发 <see cref="PropertyChanged"/>，UI 侧据此实时把参数推给渲染模块。
    /// </summary>
    public sealed class IslandConfig : INotifyPropertyChanged
    {
        private double _globalScale = 1.0;
        private double _collapsedWidth = 128;
        private double _collapsedHeight = 28;
        private double _expandedWidth = 288;
        private double _screenOffsetX;
        private double _screenOffsetY;
        private double _glassBlurStrength = 0.62;
        private double _cornerRadius = 26;
        private int _animationDurationMs = 280;
        private int _notificationDurationMs = 4000;
        private List<string> _notificationBlacklist = new List<string> { "QQ", "企业微信" };
        private bool _lyricsEnabled = true;
        private bool _takeoverNotifications = true;
        private bool _lyricsSourceNetease = true;
        private bool _lyricsSourceQQ = true;
        private bool _lyricsSourceKugou = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>全局缩放（0.5 ~ 2.0），同时作用于岛尺寸与内部排版。</summary>
        public double GlobalScale
        {
            get => _globalScale;
            set => Set(ref _globalScale, Math.Clamp(value, 0.5, 2.0));
        }

        /// <summary>收起宽度（DIP，缩放前）。</summary>
        public double CollapsedWidth
        {
            get => _collapsedWidth;
            set => Set(ref _collapsedWidth, Math.Clamp(value, 120, 480));
        }

        /// <summary>收起高度（DIP，缩放前）。</summary>
        public double CollapsedHeight
        {
            get => _collapsedHeight;
            set => Set(ref _collapsedHeight, Math.Clamp(value, 24, 96));
        }

        /// <summary>媒体态宽度（DIP，缩放前）。高度始终与空闲态一致，避免遮挡视野。</summary>
        public double ExpandedWidth
        {
            get => _expandedWidth;
            set => Set(ref _expandedWidth, Math.Clamp(value, 200, 900));
        }

        /// <summary>相对「顶部居中」停靠点的 X 偏移（DIP，正值向右）。</summary>
        public double ScreenOffsetX
        {
            get => _screenOffsetX;
            set => Set(ref _screenOffsetX, Math.Clamp(value, -3000, 3000));
        }

        /// <summary>相对屏幕工作区顶部的 Y 偏移（DIP，正值向下）。</summary>
        public double ScreenOffsetY
        {
            get => _screenOffsetY;
            set => Set(ref _screenOffsetY, Math.Clamp(value, -200, 2000));
        }

        /// <summary>
        /// 玻璃模糊强度 0..1：数值越大，玻璃越"磨砂"、背景被遮蔽得越多。
        /// 映射到渲染模块液态玻璃的染色浓度（TintAmount）。
        /// </summary>
        public double GlassBlurStrength
        {
            get => _glassBlurStrength;
            set => Set(ref _glassBlurStrength, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>圆角半径（DIP，缩放前）。</summary>
        public double CornerRadius
        {
            get => _cornerRadius;
            set => Set(ref _cornerRadius, Math.Clamp(value, 0, 80));
        }

        /// <summary>展开/收起动画时长（毫秒，0 表示无动画）。</summary>
        public int AnimationDurationMs
        {
            get => _animationDurationMs;
            set => Set(ref _animationDurationMs, Math.Clamp(value, 0, 2000));
        }

        /// <summary>通知停留时间（毫秒），超时自动移除。</summary>
        public int NotificationDurationMs
        {
            get => _notificationDurationMs;
            set => Set(ref _notificationDurationMs, Math.Clamp(value, 500, 60000));
        }

        /// <summary>通知黑名单：命中的应用名其通知直接丢弃。</summary>
        public List<string> NotificationBlacklist
        {
            get => _notificationBlacklist;
            set => Set(ref _notificationBlacklist, value ?? new List<string>());
        }

        /// <summary>是否显示歌词行。</summary>
        public bool LyricsEnabled
        {
            get => _lyricsEnabled;
            set => Set(ref _lyricsEnabled, value);
        }

        /// <summary>接管全部系统通知：岛内展示并撤下原生 Toast。</summary>
        public bool TakeoverNotifications
        {
            get => _takeoverNotifications;
            set => Set(ref _takeoverNotifications, value);
        }

        /// <summary>启用网易云音乐歌词源。</summary>
        public bool LyricsSourceNetease
        {
            get => _lyricsSourceNetease;
            set => Set(ref _lyricsSourceNetease, value);
        }

        /// <summary>启用QQ音乐歌词源。</summary>
        public bool LyricsSourceQQ
        {
            get => _lyricsSourceQQ;
            set => Set(ref _lyricsSourceQQ, value);
        }

        /// <summary>启用酷狗音乐歌词源。</summary>
        public bool LyricsSourceKugou
        {
            get => _lyricsSourceKugou;
            set => Set(ref _lyricsSourceKugou, value);
        }

        /// <summary>恢复出厂设置（逐项赋值以触发变更通知）。</summary>
        public void ResetToDefaults()
        {
            var defaults = new IslandConfig();

            GlobalScale = defaults.GlobalScale;
            CollapsedWidth = defaults.CollapsedWidth;
            CollapsedHeight = defaults.CollapsedHeight;
            ExpandedWidth = defaults.ExpandedWidth;
            ScreenOffsetX = defaults.ScreenOffsetX;
            ScreenOffsetY = defaults.ScreenOffsetY;
            GlassBlurStrength = defaults.GlassBlurStrength;
            CornerRadius = defaults.CornerRadius;
            AnimationDurationMs = defaults.AnimationDurationMs;
            NotificationDurationMs = defaults.NotificationDurationMs;
            NotificationBlacklist = new List<string>(defaults.NotificationBlacklist);
            LyricsEnabled = defaults.LyricsEnabled;
            TakeoverNotifications = defaults.TakeoverNotifications;
            LyricsSourceNetease = defaults.LyricsSourceNetease;
            LyricsSourceQQ = defaults.LyricsSourceQQ;
            LyricsSourceKugou = defaults.LyricsSourceKugou;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
