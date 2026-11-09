using System;
using System.Collections.Generic;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>一行歌词：时间戳 + 文本。</summary>
    public readonly struct LrcLine
    {
        public LrcLine(TimeSpan time, string text)
        {
            Time = time;
            Text = text;
        }

        public TimeSpan Time { get; }
        public string Text { get; }
    }

    /// <summary>
    /// LRC 歌词文档：解析标准/扩展 LRC（支持一行多时间戳、[offset:] 整体偏移），
    /// 按时间排序后提供二分查找定位当前行。
    /// </summary>
    public sealed class LrcDocument
    {
        public static readonly LrcDocument Empty = new LrcDocument(Array.Empty<LrcLine>());

        private readonly LrcLine[] _lines;

        private LrcDocument(LrcLine[] lines)
        {
            _lines = lines;
        }

        public IReadOnlyList<LrcLine> Lines => _lines;

        public bool IsEmpty => _lines.Length == 0;

        /// <summary>解析 LRC 文本；无法解析时返回 <see cref="Empty"/>。</summary>
        public static LrcDocument Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Empty;
            }

            var lines = new List<LrcLine>();
            int offsetMs = 0;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var stamps = new List<TimeSpan>();
                int index = 0;

                while (index < line.Length && line[index] == '[')
                {
                    int close = line.IndexOf(']', index);
                    if (close < 0)
                    {
                        break;
                    }

                    string tag = line.Substring(index + 1, close - index - 1).Trim();
                    if (TryParseTimestamp(tag, out TimeSpan stamp))
                    {
                        stamps.Add(stamp);
                    }
                    else if (TryParseOffset(tag, out int offset))
                    {
                        offsetMs = offset;
                    }

                    index = close + 1;
                }

                if (stamps.Count == 0)
                {
                    continue;
                }

                string content = line.Substring(index).Trim();
                foreach (TimeSpan stamp in stamps)
                {
                    lines.Add(new LrcLine(stamp, content));
                }
            }

            if (lines.Count == 0)
            {
                return Empty;
            }

            // 去掉 [00:00] 作词/作曲 这类制作信息行：它们不是歌词，
            // 显示在岛上会占掉唯一的一行歌词位。全部是元信息时保留原样。
            var lyricOnly = lines.FindAll(line => !IsMetadataLine(line.Text));
            if (lyricOnly.Count > 0)
            {
                lines = lyricOnly;
            }

            if (offsetMs != 0)
            {
                var shift = TimeSpan.FromMilliseconds(offsetMs);
                for (int i = 0; i < lines.Count; i++)
                {
                    lines[i] = new LrcLine(lines[i].Time + shift, lines[i].Text);
                }
            }

            lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return new LrcDocument(lines.ToArray());
        }

        /// <summary>返回 time 时刻应显示的歌词行下标；尚未到第一行时返回 -1。</summary>
        public int LineAt(TimeSpan time)
        {
            if (_lines.Length == 0 || time < _lines[0].Time)
            {
                return -1;
            }

            int lo = 0;
            int hi = _lines.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_lines[mid].Time <= time)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }

        private static readonly string[] MetadataPrefixes =
        {
            "作词", "作曲", "编曲", "制作", "出品", "监制", "混音", "母带", "录音",
            "和声", "吉他", "贝斯", "鼓", "词", "曲", "OP", "SP", "发行", "统筹", "策划",
        };

        /// <summary>判断是否为「作词：xxx」这类制作信息行（前缀后必须是冒号或空白）。</summary>
        private static bool IsMetadataLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            string trimmed = text.TrimStart();
            foreach (string prefix in MetadataPrefixes)
            {
                if (trimmed.Length <= prefix.Length
                    || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                char next = trimmed[prefix.Length];
                if (next == ':' || next == '：' || next == '/' || char.IsWhiteSpace(next))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseTimestamp(string tag, out TimeSpan time)
        {
            time = default;

            // 形如 01:23 / 01:23.45 / 01:23:456（冒号或点号分隔小数）
            int colon = tag.IndexOf(':');
            if (colon <= 0 || colon == tag.Length - 1)
            {
                return false;
            }

            string minutePart = tag.Substring(0, colon);
            if (!int.TryParse(minutePart, out int minutes) || minutes < 0 || minutes > 599)
            {
                return false;
            }

            string rest = tag.Substring(colon + 1);
            int dot = rest.IndexOfAny(new[] { '.', ':' });
            string secondPart = dot < 0 ? rest : rest.Substring(0, dot);
            if (!int.TryParse(secondPart, out int seconds) || seconds < 0 || seconds > 59)
            {
                return false;
            }

            int millis = 0;
            if (dot >= 0 && dot < rest.Length - 1)
            {
                string fraction = rest.Substring(dot + 1);
                if (!int.TryParse(fraction, out int fractionValue) || fractionValue < 0)
                {
                    return false;
                }

                millis = fraction.Length switch
                {
                    1 => fractionValue * 100,
                    2 => fractionValue * 10,
                    3 => fractionValue,
                    _ => fractionValue / (int)Math.Pow(10, fraction.Length - 3),
                };
            }

            time = TimeSpan.FromMilliseconds(minutes * 60000.0 + seconds * 1000.0 + millis);
            return true;
        }

        private static bool TryParseOffset(string tag, out int offsetMs)
        {
            offsetMs = 0;
            if (!tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(tag.Substring(7).Trim(), out offsetMs);
        }
    }
}
