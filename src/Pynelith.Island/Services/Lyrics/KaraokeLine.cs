using System;
using System.Collections.Generic;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>逐字（音节）时间片。</summary>
    public readonly struct LrcWord
    {
        public LrcWord(TimeSpan time, int durationMs, string text)
        {
            Time = time;
            DurationMs = durationMs;
            Text = text;
        }

        /// <summary>该字/词的起始时间。</summary>
        public TimeSpan Time { get; }

        /// <summary>该字/词的持续时长（毫秒）。</summary>
        public int DurationMs { get; }

        /// <summary>该字/词的文本。</summary>
        public string Text { get; }

        /// <summary>该字/词的结束时间。</summary>
        public TimeSpan End => Time + TimeSpan.FromMilliseconds(DurationMs);
    }

    /// <summary>
    /// 逐字歌词行：既有整行的时间戳，也有每个字的时间戳。
    ///
    /// 两种来源：
    /// - 真逐字：QQ 的 QRC / 网易云的 YRC 带每字时间戳，直接用；
    /// - 插值逐字：普通 LRC 只有行时间戳，按字数在行内均分（卡拉OK 的常见近似）。
    /// </summary>
    public sealed class KaraokeLine
    {
        public KaraokeLine(TimeSpan start, IReadOnlyList<LrcWord> words, bool interpolated)
        {
            Start = start;
            Words = words;
            IsInterpolated = interpolated;
        }

        /// <summary>整行起始时间。</summary>
        public TimeSpan Start { get; }

        /// <summary>逐字时间片。</summary>
        public IReadOnlyList<LrcWord> Words { get; }

        /// <summary>是否为插值生成（true = 原始歌词没有逐字时间戳）。</summary>
        public bool IsInterpolated { get; }

        /// <summary>整行文本。</summary>
        public string Text
        {
            get
            {
                if (Words.Count == 0)
                {
                    return string.Empty;
                }

                var builder = new System.Text.StringBuilder();
                foreach (var word in Words)
                {
                    builder.Append(word.Text);
                }

                return builder.ToString();
            }
        }

        /// <summary>整行结束时间。</summary>
        public TimeSpan End
        {
            get
            {
                if (Words.Count == 0)
                {
                    return Start;
                }

                TimeSpan end = Words[0].End;
                foreach (var word in Words)
                {
                    if (word.End > end)
                    {
                        end = word.End;
                    }
                }

                return end;
            }
        }

        /// <summary>
        /// 计算某一时刻已唱到的字数比例（0..1），用于卡拉 OK 高亮进度。
        /// 行首之前返回 0，行尾之后返回 1。
        /// </summary>
        public double ProgressAt(TimeSpan time)
        {
            if (Words.Count == 0)
            {
                return 0;
            }

            if (time <= Start)
            {
                return 0;
            }

            if (time >= End)
            {
                return 1;
            }

            // 找到当前所在的时间片
            for (int i = 0; i < Words.Count; i++)
            {
                var word = Words[i];
                if (time < word.Time)
                {
                    return (double)i / Words.Count;
                }

                if (time <= word.End)
                {
                    // 落在当前字内：算上这一字内部的进度
                    double within = word.DurationMs <= 0
                        ? 0
                        : (time - word.Time).TotalMilliseconds / word.DurationMs;
                    return (i + Math.Clamp(within, 0, 1)) / Words.Count;
                }
            }

            return 1;
        }

        /// <summary>
        /// 按进度比例切出「已唱」与「未唱」两段文本。
        /// 按字数切分而不是按字符宽度切分：中文歌词以字为单位最自然。
        /// </summary>
        public (string Sung, string Unsung) SplitAt(TimeSpan time)
        {
            if (Words.Count == 0)
            {
                return (string.Empty, string.Empty);
            }

            double progress = ProgressAt(time);
            int sungCount = (int)Math.Floor(progress * Words.Count);
            sungCount = Math.Clamp(sungCount, 0, Words.Count);

            var sung = new System.Text.StringBuilder();
            var unsung = new System.Text.StringBuilder();

            for (int i = 0; i < Words.Count; i++)
            {
                if (i < sungCount)
                {
                    sung.Append(Words[i].Text);
                }
                else
                {
                    unsung.Append(Words[i].Text);
                }
            }

            return (sung.ToString(), unsung.ToString());
        }
    }
}
