using System;
using System.Collections.Generic;

namespace Pynelith.Island.Services
{
    /// <summary>一条剪贴板记录。</summary>
    public sealed class ClipboardEntry
    {
        public ClipboardEntry(string text, DateTime capturedAt)
        {
            Text = text;
            CapturedAt = capturedAt;
        }

        /// <summary>原始文本（可能包含换行）。</summary>
        public string Text { get; }

        /// <summary>采集时间。</summary>
        public DateTime CapturedAt { get; }

        /// <summary>单行预览：换行折成空格，便于列表里一行省略显示。</summary>
        public string Preview => Text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    /// <summary>
    /// 剪贴板历史（纯逻辑，不含任何平台调用，便于单测）。
    /// 规则：空白内容忽略；重复内容移到最前（不重复占位）；超出上限丢弃最旧的。
    /// </summary>
    public sealed class ClipboardHistory
    {
        /// <summary>单条文本最大保留长度，超长内容截断，避免常驻内存被一张大表格撑爆。</summary>
        public const int MaxTextLength = 20000;

        private readonly object _gate = new object();
        private readonly List<ClipboardEntry> _items = new List<ClipboardEntry>();
        private int _limit = 20;

        /// <summary>历史发生变化（添加 / 清空 / 上限变化）。</summary>
        public event EventHandler? Changed;

        /// <summary>保留条数上限（1 ~ 200）。</summary>
        public int Limit
        {
            get => _limit;
            set
            {
                int clamped = Math.Clamp(value, 1, 200);
                lock (_gate)
                {
                    if (_limit == clamped)
                    {
                        return;
                    }

                    _limit = clamped;
                    TrimLocked();
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>历史条目，最新的在最前。</summary>
        public IReadOnlyList<ClipboardEntry> Items
        {
            get
            {
                lock (_gate)
                {
                    return _items.ToArray();
                }
            }
        }

        /// <summary>记录一段文本；被忽略或与原内容重复时返回 false。</summary>
        public bool Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.Length > MaxTextLength)
            {
                text = text.Substring(0, MaxTextLength);
            }

            lock (_gate)
            {
                // 已在最前：说明是同一个内容反复复制，不刷新时间也不重复插入
                if (_items.Count > 0 && string.Equals(_items[0].Text, text, StringComparison.Ordinal))
                {
                    return false;
                }

                _items.RemoveAll(e => string.Equals(e.Text, text, StringComparison.Ordinal));
                _items.Insert(0, new ClipboardEntry(text, DateTime.Now));
                TrimLocked();
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>清空全部历史。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                if (_items.Count == 0)
                {
                    return;
                }

                _items.Clear();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void TrimLocked()
        {
            while (_items.Count > _limit)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }
    }
}
