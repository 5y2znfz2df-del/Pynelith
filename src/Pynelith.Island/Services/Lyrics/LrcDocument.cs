using System;
using System.Collections.Generic;
using System.Linq;

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
    ///
    /// 另外支持逐字歌词格式：
    /// - QQ 音乐 QRC：<c>[行起始ms,行时长ms]字(字起始ms,字时长ms)…</c>
    /// - 网易云 YRC：<c>[行起始ms,行时长ms](字起始ms,字时长ms,0)字…</c>
    /// 解析不到逐字时间戳时，退化为按字数在行内均分（插值逐字）。
    /// </summary>
    public sealed class LrcDocument
    {
        public static readonly LrcDocument Empty = new LrcDocument(Array.Empty<LrcLine>());

        private readonly LrcLine[] _lines;
        private readonly KaraokeLine[] _karaoke;

        private LrcDocument(LrcLine[] lines, KaraokeLine[]? karaoke = null)
        {
            _lines = lines;
            _karaoke = karaoke ?? Array.Empty<KaraokeLine>();
        }

        public IReadOnlyList<LrcLine> Lines => _lines;

        public bool IsEmpty => _lines.Length == 0;

        /// <summary>逐字歌词行（与 <see cref="Lines"/> 一一对应；无逐字数据时为空）。</summary>
        public IReadOnlyList<KaraokeLine> KaraokeLines => _karaoke;

        /// <summary>该文档是否带逐字时间戳（来自 QRC / YRC 源）。</summary>
        public bool HasWordTiming => _karaoke.Length > 0;

        /// <summary>解析 LRC 文本；无法解析时返回 <see cref="Empty"/>。</summary>
        public static LrcDocument Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Empty;
            }

            // 优先按逐字格式解析（QRC / YRC）；解析不出来再退回标准 LRC。
            KaraokeLine[]? wordLines = TryParseWordTimed(text);
            if (wordLines != null && wordLines.Length > 0)
            {
                return BuildFromWordTimed(wordLines);
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

            // 标准 LRC 没有逐字时间戳：按字数在行内均分，生成插值逐字行。
            var karaoke = BuildInterpolatedKaraoke(lines);
            return new LrcDocument(lines.ToArray(), karaoke);
        }

        /// <summary>
        /// 解析逐字歌词（QQ QRC / 网易云 YRC）。
        /// 不是这两种格式时返回 null。
        /// </summary>
        private static KaraokeLine[]? TryParseWordTimed(string text)
        {
            var result = new List<KaraokeLine>();

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!TryParseWordTimedLine(line, out var karaokeLine))
                {
                    continue;
                }

                if (karaokeLine.Words.Count > 0)
                {
                    result.Add(karaokeLine);
                }
            }

            if (result.Count == 0)
            {
                return null;
            }

            result.Sort((a, b) => a.Start.CompareTo(b.Start));
            return result.ToArray();
        }

        /// <summary>
        /// 解析单行逐字歌词。
        ///
        /// QQ QRC：<c>[起始ms,时长ms]字(字起始ms,字时长ms)字(…)</c>
        /// 网易云 YRC：<c>[起始ms,时长ms](字起始ms,字时长ms,0)字(…)</c>
        ///
        /// 两种格式的区别只在字时间戳是 <c>(a,b)</c> 还是 <c>(a,b,0)</c>，
        /// 因此用同一个解析器处理。
        /// </summary>
        private static bool TryParseWordTimedLine(string line, out KaraokeLine result)
        {
            result = null!;

            int close = line.IndexOf(']');
            if (close <= 0)
            {
                return false;
            }

            string header = line[1..close];
            int comma = header.IndexOf(',');
            if (comma <= 0)
            {
                return false;
            }

            if (!int.TryParse(header[..comma].Trim(), out int lineStartMs)
                || !int.TryParse(header[(comma + 1)..].Trim(), out int lineDurationMs))
            {
                return false;
            }

            string body = line[(close + 1)..];
            if (body.Length == 0)
            {
                return false;
            }

            var words = new List<LrcWord>();

            // 两种逐字格式的字时间戳与文字的相对位置**相反**：
            //   QQ QRC ：文字在前  →  字(起始ms,时长ms)字(…)
            //   网易 YRC：时间戳在前 →  (起始ms,时长ms,0)字(…)
            // 用 body 是否以 '(' 开头来区分，分别走两条解析路径，
            // 否则会把每个字的文本错位到后一个字上。
            bool timestampFirst = body.TrimStart().StartsWith("(", StringComparison.Ordinal);

            if (timestampFirst)
            {
                ParseTimestampFirst(body, words);
            }
            else
            {
                ParseTextFirst(body, words);
            }

            if (words.Count == 0)
            {
                return false;
            }

            // 给没有独立时间戳的片段补时间：用相邻已知时间戳线性插值。
            var timed = new List<LrcWord>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (word.Time != TimeSpan.Zero || word.DurationMs != 0)
                {
                    timed.Add(word);
                    continue;
                }

                // 向前找最近的已知时间戳
                int prev = -1;
                for (int p = i - 1; p >= 0; p--)
                {
                    if (words[p].Time != TimeSpan.Zero || words[p].DurationMs != 0)
                    {
                        prev = p;
                        break;
                    }
                }

                // 向后找最近的已知时间戳
                int next = -1;
                for (int p = i + 1; p < words.Count; p++)
                {
                    if (words[p].Time != TimeSpan.Zero || words[p].DurationMs != 0)
                    {
                        next = p;
                        break;
                    }
                }

                if (prev >= 0 && next > prev)
                {
                    double ratio = (double)(i - prev) / (next - prev);
                    double prevEnd = words[prev].Time.TotalMilliseconds + words[prev].DurationMs;
                    double nextStart = words[next].Time.TotalMilliseconds;
                    double start = prevEnd + (nextStart - prevEnd) * ratio;
                    double duration = Math.Max(0, nextStart - start);
                    timed.Add(new LrcWord(
                        TimeSpan.FromMilliseconds(start),
                        (int)duration,
                        word.Text));
                }
                else if (prev >= 0)
                {
                    // 后面没有已知时间戳：沿用前一个字的时长
                    timed.Add(new LrcWord(
                        words[prev].Time + TimeSpan.FromMilliseconds(words[prev].DurationMs),
                        words[prev].DurationMs,
                        word.Text));
                }
                else
                {
                    // 前面没有已知时间戳：从行首开始平分
                    int count = next - i + 1;
                    double span = lineDurationMs / (double)Math.Max(1, count);
                    timed.Add(new LrcWord(
                        TimeSpan.FromMilliseconds(lineStartMs + (i - 0) * span),
                        (int)span,
                        word.Text));
                }
            }

            result = new KaraokeLine(
                TimeSpan.FromMilliseconds(lineStartMs),
                timed,
                interpolated: false);
            return true;
        }

        /// <summary>QQ QRC：文字在前，时间戳在后 —— <c>字(起始ms,时长ms)</c>。</summary>
        private static void ParseTextFirst(string body, List<LrcWord> words)
        {
            var pending = new System.Text.StringBuilder();
            int index = 0;

            while (index < body.Length)
            {
                if (body[index] != '(')
                {
                    pending.Append(body[index]);
                    index++;
                    continue;
                }

                int end = body.IndexOf(')', index);
                if (end < 0)
                {
                    break;
                }

                if (TryParseWordStamp(body[(index + 1)..end], out int startMs, out int durationMs))
                {
                    string text = pending.ToString();
                    pending.Clear();
                    if (text.Length > 0)
                    {
                        words.Add(new LrcWord(TimeSpan.FromMilliseconds(startMs), durationMs, text));
                    }
                }

                index = end + 1;
            }

            // 行尾仍有没被时间戳领走的文字
            if (pending.Length > 0)
            {
                words.Add(new LrcWord(TimeSpan.Zero, 0, pending.ToString()));
            }
        }

        /// <summary>网易 YRC：时间戳在前，文字在后 —— <c>(起始ms,时长ms,0)字</c>。</summary>
        private static void ParseTimestampFirst(string body, List<LrcWord> words)
        {
            int index = 0;

            while (index < body.Length)
            {
                if (body[index] != '(')
                {
                    index++;
                    continue;
                }

                int end = body.IndexOf(')', index);
                if (end < 0)
                {
                    break;
                }

                int textStart = end + 1;
                int textEnd = body.IndexOf('(', textStart);
                if (textEnd < 0)
                {
                    textEnd = body.Length;
                }

                string text = body[textStart..textEnd];

                if (TryParseWordStamp(body[(index + 1)..end], out int startMs, out int durationMs)
                    && text.Length > 0)
                {
                    words.Add(new LrcWord(TimeSpan.FromMilliseconds(startMs), durationMs, text));
                }

                index = textEnd;
            }
        }

        /// <summary>解析字时间戳「起始ms,时长ms」或「起始ms,时长ms,0」。</summary>
        private static bool TryParseWordStamp(string stamp, out int startMs, out int durationMs)
        {
            startMs = 0;
            durationMs = 0;

            string[] parts = stamp.Split(',');
            if (parts.Length < 2)
            {
                return false;
            }

            return int.TryParse(parts[0].Trim(), out startMs)
                && int.TryParse(parts[1].Trim(), out durationMs);
        }

        /// <summary>由标准 LRC 行生成插值逐字行：按字数在行内均分。</summary>
        private static KaraokeLine[] BuildInterpolatedKaraoke(List<LrcLine> lines)
        {
            var result = new List<KaraokeLine>(lines.Count);

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                // 本行的结束时间 = 下一行的开始时间；最后一行按 4 秒估算
                TimeSpan end = i + 1 < lines.Count
                    ? lines[i + 1].Time
                    : line.Time + TimeSpan.FromSeconds(4);

                double spanMs = (end - line.Time).TotalMilliseconds;
                if (spanMs <= 0)
                {
                    spanMs = 1;
                }

                string text = line.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 空行也要占位，保证逐字行与整行下标一一对应
                    result.Add(new KaraokeLine(line.Time, Array.Empty<LrcWord>(), interpolated: true));
                    continue;
                }

                // 按"字"切分：中文按字、英文按单词
                var chunks = SplitIntoChunks(text);
                if (chunks.Count == 0)
                {
                    result.Add(new KaraokeLine(line.Time, Array.Empty<LrcWord>(), interpolated: true));
                    continue;
                }

                double perWord = spanMs / chunks.Count;
                var words = new List<LrcWord>(chunks.Count);

                for (int c = 0; c < chunks.Count; c++)
                {
                    words.Add(new LrcWord(
                        line.Time + TimeSpan.FromMilliseconds(perWord * c),
                        (int)Math.Round(perWord),
                        chunks[c]));
                }

                result.Add(new KaraokeLine(line.Time, words, interpolated: true));
            }

            return result.ToArray();
        }

        /// <summary>
        /// 把一行歌词切成"卡拉 OK 单元"：中文按字、英文按单词、标点独立成块。
        /// 空白并入前一个单元，保证所有单元拼起来与原文完全一致。
        /// </summary>
        private static List<string> SplitIntoChunks(string text)
        {
            var chunks = new List<string>();
            var latin = new System.Text.StringBuilder();

            void FlushLatin()
            {
                if (latin.Length > 0)
                {
                    chunks.Add(latin.ToString());
                    latin.Clear();
                }
            }

            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (ch < 128)
                    {
                        // 拉丁字母/数字：连续的一段算一个单词
                        latin.Append(ch);
                    }
                    else
                    {
                        // 中日韩等：每个字单独成为一个单元
                        FlushLatin();
                        chunks.Add(ch.ToString());
                    }

                    continue;
                }

                FlushLatin();

                if (char.IsWhiteSpace(ch))
                {
                    // 空白并入前一个单元（拼回去与原文一致）
                    if (chunks.Count > 0)
                    {
                        chunks[chunks.Count - 1] += ch;
                    }
                    else
                    {
                        chunks.Add(ch.ToString());
                    }

                    continue;
                }

                // 标点：独立成块
                chunks.Add(ch.ToString());
            }

            FlushLatin();
            return chunks;
        }

        /// <summary>由逐字行构建文档：整行文本由逐字时间片拼出。</summary>
        private static LrcDocument BuildFromWordTimed(KaraokeLine[] karaoke)
        {
            var lines = new List<LrcLine>(karaoke.Length);
            var kept = new List<KaraokeLine>(karaoke.Length);

            foreach (var line in karaoke)
            {
                string text = line.Text;
                if (string.IsNullOrWhiteSpace(text) || IsMetadataLine(text))
                {
                    continue;
                }

                kept.Add(line);
                lines.Add(new LrcLine(line.Start, text));
            }

            if (lines.Count == 0)
            {
                // 全部是元信息：保留原样，不丢内容
                var fallback = karaoke.Select(l => new LrcLine(l.Start, l.Text)).ToArray();
                return new LrcDocument(fallback, karaoke);
            }

            // 逐字行与整行严格一一对应（过滤元信息时同步过滤），
            // 调用方才能用同一个下标同时取整行文本与逐字时间片。
            return new LrcDocument(lines.ToArray(), kept.ToArray());
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

        /// <summary>
        /// 返回 time 时刻对应的逐字行（卡拉 OK 用）。
        /// 用 <paramref name="lineIndex"/> 直接按下标取，
        /// 保证与 <see cref="Lines"/> 严格一一对应。
        /// </summary>
        public KaraokeLine? KaraokeAt(TimeSpan time, int lineIndex)
        {
            if (_karaoke.Length == 0)
            {
                return null;
            }

            if (lineIndex >= 0 && lineIndex < _karaoke.Length)
            {
                return _karaoke[lineIndex];
            }

            // 下标对不上（理论上不会发生）：退回到按时间二分查找
            if (time < _karaoke[0].Start)
            {
                return null;
            }

            int lo = 0;
            int hi = _karaoke.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_karaoke[mid].Start <= time)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return _karaoke[lo];
        }

        private static readonly string[] MetadataPrefixes =
        {
            "作词", "作曲", "编曲", "制作", "出品", "监制", "混音", "母带", "录音",
            "和声", "吉他", "贝斯", "鼓", "词", "曲", "OP", "SP", "发行", "统筹", "策划",
            // 部分 LRC 会把歌手/专辑信息写进第一行，不能当作歌词显示
            "演唱", "歌手", "专辑",
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
