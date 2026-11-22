using System;
using System.Windows.Media;

namespace Pynelith.Island.Models
{
    /// <summary>
    /// 一条系统通知。
    ///
    /// 字段用途：
    /// - <see cref="Id"/>：系统通知 id；
    /// - <see cref="Title"/>：通知标题（ToastGeneric 的第一个文本元素）；
    /// - <see cref="Body"/>：通知正文；
    /// - <see cref="AppKey"/>：分组键（同一应用的通知归为一组）。
    /// </summary>
    public sealed class NotificationItem
    {
        /// <summary>系统通知 id。</summary>
        public uint Id { get; init; }

        /// <summary>应用显示名，例如"微信"。</summary>
        public string AppName { get; init; } = string.Empty;

        /// <summary>分组键：同一应用的通知归为一组。用 AUMID 优先，退化到显示名。</summary>
        public string AppKey { get; init; } = string.Empty;

        /// <summary>通知标题（可能为空）。</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>通知正文。</summary>
        public string Body { get; init; } = string.Empty;

        /// <summary>应用强调色，用于左侧圆点。</summary>
        public Brush Accent { get; init; } = Brushes.Transparent;

        /// <summary>到达时间，用于超时移除与历史排序。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>右上角简短时间。</summary>
        public string TimeText => CreatedAt.ToString("HH:mm");

        /// <summary>标题 + 正文拼成的完整文本。</summary>
        public string FullText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Title))
                {
                    return Body;
                }

                if (string.IsNullOrWhiteSpace(Body))
                {
                    return Title;
                }

                return Title + " · " + Body;
            }
        }
    }
}
