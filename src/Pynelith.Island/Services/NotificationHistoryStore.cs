using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Pynelith.Island.Models;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 通知历史记录：按应用分组，持久化到 %AppData%\Pynelith\notifications.json。
    ///
    /// 这是「接管完整落地」里最基础的一块：
    /// 岛内只显示一条消息，错过就没了；有了历史，用户可以随时回看
    /// "刚才那个通知到底是什么"。
    ///
    /// 落盘策略：每次变更后防抖 500ms 写一次，原子替换（临时文件 + Move），
    /// 与配置文件的写法保持一致。
    /// </summary>
    public sealed class NotificationHistoryStore : IDisposable
    {
        /// <summary>历史保留条数上限（超出后丢弃最旧的）。</summary>
        public const int MaxEntries = 200;

        /// <summary>保留的历史时长（超过则不再显示在列表里，但文件仍保留）。</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
        };

        private readonly object _gate = new object();
        private readonly List<NotificationItem> _entries = new List<NotificationItem>();
        private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
        private bool _disposed;

        public NotificationHistoryStore()
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _saveTimer.Tick += (_, _) => SaveNow();

            Load();
        }

        /// <summary>历史发生变化时触发（UI 线程）。</summary>
        public event EventHandler? Changed;

        /// <summary>按时间倒序的全部历史条目。</summary>
        public IReadOnlyList<NotificationItem> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.OrderByDescending(e => e.CreatedAt).ToArray();
                }
            }
        }

        /// <summary>
        /// 按应用分组的历史：组内按时间倒序，组间按最新一条的时间倒序。
        /// </summary>
        public IReadOnlyList<NotificationGroup> Groups
        {
            get
            {
                var grouped = new Dictionary<string, List<NotificationItem>>(StringComparer.Ordinal);

                lock (_gate)
                {
                    foreach (var entry in _entries)
                    {
                        string key = string.IsNullOrEmpty(entry.AppKey) ? entry.AppName : entry.AppKey;
                        if (!grouped.TryGetValue(key, out var list))
                        {
                            list = new List<NotificationItem>();
                            grouped[key] = list;
                        }

                        list.Add(entry);
                    }
                }

                var groups = new List<NotificationGroup>();
                foreach (var pair in grouped)
                {
                    var ordered = pair.Value.OrderByDescending(e => e.CreatedAt).ToArray();
                    groups.Add(new NotificationGroup(
                        pair.Key,
                        ordered[0].AppName,
                        ordered[0].Accent,
                        ordered,
                        ordered[0].CreatedAt));
                }

                return groups
                    .OrderByDescending(g => g.LatestAt)
                    .ToArray();
            }
        }

        /// <summary>记录一条通知（会触发落盘与刷新）。</summary>
        public void Add(NotificationItem item)
        {
            if (item == null)
            {
                return;
            }

            lock (_gate)
            {
                _entries.Add(item);

                // 超量时丢弃最旧的
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveAt(0);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }

        /// <summary>清空全部历史。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    return;
                }

                _entries.Clear();
            }

            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }

        /// <summary>移除某条历史（用户手动清掉单条）。</summary>
        public void Remove(uint id)
        {
            lock (_gate)
            {
                _entries.RemoveAll(e => e.Id == id);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (!_disposed)
            {
                _saveTimer.Stop();
                _saveTimer.Start();
            }
        }

        /// <summary>历史文件路径。</summary>
        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pynelith",
            "notifications.json");

        private void Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return;
                }

                string json = File.ReadAllText(path);
                var saved = JsonSerializer.Deserialize<List<SavedEntry>>(json, JsonOptions);
                if (saved == null)
                {
                    return;
                }

                DateTime cutoff = DateTime.Now - MaxAge;

                lock (_gate)
                {
                    foreach (var item in saved)
                    {
                        // 只恢复未过期的
                        if (item.CreatedAt < cutoff)
                        {
                            continue;
                        }

                        _entries.Add(new NotificationItem
                        {
                            Id = item.Id,
                            AppName = item.AppName,
                            AppKey = item.AppKey,
                            Title = item.Title,
                            Body = item.Body,
                            CreatedAt = item.CreatedAt,
                        });
                    }
                }
            }
            catch (Exception)
            {
                // 历史文件损坏时忽略，不影响新通知的接收。
            }
        }

        private void SaveNow()
        {
            _saveTimer.Stop();

            if (_disposed)
            {
                return;
            }

            try
            {
                List<SavedEntry> saved;
                lock (_gate)
                {
                    saved = _entries
                        .Select(e => new SavedEntry
                        {
                            Id = e.Id,
                            AppName = e.AppName,
                            AppKey = e.AppKey,
                            Title = e.Title,
                            Body = e.Body,
                            CreatedAt = e.CreatedAt,
                        })
                        .ToList();
                }

                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // 原子替换：先写临时文件再 Move，避免写到一半崩溃导致文件损坏
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(saved, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // 落盘失败不影响使用（历史只是辅助功能）。
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SaveNow();
            _saveTimer.Stop();
        }

        /// <summary>落盘用的简化结构（不含运行时字段，如 Brush）。</summary>
        private sealed class SavedEntry
        {
            public uint Id { get; set; }
            public string AppName { get; set; } = string.Empty;
            public string AppKey { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
        }
    }

    /// <summary>按应用分组后的通知集合。</summary>
    public sealed class NotificationGroup
    {
        public NotificationGroup(
            string key,
            string appName,
            System.Windows.Media.Brush accent,
            IReadOnlyList<NotificationItem> items,
            DateTime latestAt)
        {
            Key = key;
            AppName = appName;
            Accent = accent;
            Items = items;
            LatestAt = latestAt;
        }

        /// <summary>分组键。</summary>
        public string Key { get; }

        /// <summary>应用显示名。</summary>
        public string AppName { get; }

        /// <summary>应用强调色。</summary>
        public System.Windows.Media.Brush Accent { get; }

        /// <summary>组内通知，按时间倒序。</summary>
        public IReadOnlyList<NotificationItem> Items { get; }

        /// <summary>组内最新一条的时间。</summary>
        public DateTime LatestAt { get; }

        /// <summary>未读数量（本会话内新到的条数）。</summary>
        public int UnreadCount { get; internal set; }
    }
}
