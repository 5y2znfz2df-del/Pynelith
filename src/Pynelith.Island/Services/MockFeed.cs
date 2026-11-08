using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Pynelith.Island.Configuration;
using Pynelith.Island.Models;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 模拟数据源：按固定节奏产生通知、轮播歌词。
    /// 本阶段不接真实后台通知/媒体播放，界面调试全部基于它。
    /// </summary>
    public sealed class MockFeed : IDisposable
    {
        private static readonly (string App, string Title, string Accent)[] Pool =
        {
            ("微信", "新消息 · 今晚一起吃饭？", "#4ADE80"),
            ("邮件", "3 封新邮件 · 设计评审纪要", "#60A5FA"),
            ("日历", "15:30 产品周会 · 会议室 A", "#FBBF24"),
            ("QQ", "群消息 12 条", "#38BDF8"),
            ("系统", "电量低于 20%，请连接电源", "#F87171"),
            ("音乐", "开始播放 · Ambient Loop", "#C084FC"),
        };

        private static readonly string[] Lyrics =
        {
            "夜色在玻璃边缘缓缓流过",
            "光在折射里碎成三种颜色",
            "把屏幕外的世界调成静音",
            "我们只需要一小块安静",
        };

        private readonly IslandConfig _config;
        private readonly DispatcherTimer _arrivalTimer;
        private readonly DispatcherTimer _sweepTimer;
        private readonly DispatcherTimer _lyricTimer;
        private int _nextNotification;
        private int _nextLyric;
        private bool _disposed;

        public MockFeed(IslandConfig config)
        {
            _config = config;

            // 先铺两条内容，界面一打开就有东西可看。
            Push(Pool[0]);
            Push(Pool[1]);

            _arrivalTimer = CreateTimer(TimeSpan.FromSeconds(6), OnArrivalTick);
            _sweepTimer = CreateTimer(TimeSpan.FromSeconds(1), OnSweepTick);
            _lyricTimer = CreateTimer(TimeSpan.FromSeconds(3.5), OnLyricTick);
        }

        /// <summary>当前通知列表，最新的在最前。</summary>
        public ObservableCollection<NotificationItem> Notifications { get; } = new ObservableCollection<NotificationItem>();

        /// <summary>当前曲目（含歌词行）。</summary>
        public TrackInfo Track { get; } = new TrackInfo();

        private DispatcherTimer CreateTimer(TimeSpan interval, EventHandler handler)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            timer.Tick += handler;
            timer.Start();
            return timer;
        }

        private void OnArrivalTick(object? sender, EventArgs e)
        {
            var candidate = Pool[_nextNotification++ % Pool.Length];

            if (IsBlacklisted(candidate.App))
            {
                return;
            }

            Push(candidate);
        }

        private void OnSweepTick(object? sender, EventArgs e)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(-_config.NotificationDurationMs);
            for (int i = Notifications.Count - 1; i >= 0; i--)
            {
                if (Notifications[i].CreatedAt < deadline)
                {
                    Notifications.RemoveAt(i);
                }
            }
        }

        private void OnLyricTick(object? sender, EventArgs e)
        {
            Track.Lyric = Lyrics[_nextLyric++ % Lyrics.Length];
        }

        private void Push((string App, string Title, string Accent) source)
        {
            Notifications.Insert(0, new NotificationItem
            {
                AppName = source.App,
                Title = source.Title,
                Accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(source.Accent)),
                CreatedAt = DateTime.Now,
            });

            // 面板只展示最近 3 条。
            while (Notifications.Count > 3)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }
        }

        private bool IsBlacklisted(string appName)
        {
            List<string> blacklist = _config.NotificationBlacklist;
            return blacklist != null
                   && blacklist.Any(name => string.Equals(name, appName, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _arrivalTimer.Stop();
            _sweepTimer.Stop();
            _lyricTimer.Stop();
        }
    }
}
