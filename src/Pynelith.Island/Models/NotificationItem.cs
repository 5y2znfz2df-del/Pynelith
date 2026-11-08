using System;
using System.Windows.Media;

namespace Pynelith.Island.Models
{
    /// <summary>一条通知（当前阶段由模拟数据源生成）。</summary>
    public sealed class NotificationItem
    {
        public string AppName { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        /// <summary>应用强调色，用于左侧圆点。</summary>
        public Brush Accent { get; init; } = Brushes.Transparent;

        /// <summary>到达时间，用于超时移除。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>右上角简短时间。</summary>
        public string TimeText => CreatedAt.ToString("HH:mm");
    }
}
