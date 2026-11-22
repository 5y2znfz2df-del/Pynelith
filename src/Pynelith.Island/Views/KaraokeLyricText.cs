using System;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 卡拉 OK 逐字高亮文本。
    ///
    /// 实现方式：把一行歌词拆成「已唱」「未唱」两段，用两个 Inline 拼在同一个 TextBlock 里：
    /// - 已唱部分用高亮色；
    /// - 未唱部分用弱化色。
    ///
    /// 两段在同一个 TextBlock 内渲染，因此不会出现两段字体基线不齐的问题；
    /// 文字没变时只改颜色，不触发重新排版。
    ///
    /// 之所以不直接改 TextBlock.Foreground：逐字高亮需要一行内呈现两种颜色，
    /// 用 Inline 是开销最小的做法。
    /// </summary>
    internal sealed class KaraokeLyricText
    {
        private readonly Run _sungRun;
        private readonly Run _unsungRun;

        private string _currentText = string.Empty;

        public KaraokeLyricText(TextBlock host, Color sungColor, Color unsungColor)
        {
            _sungRun = new Run { Foreground = new SolidColorBrush(sungColor) };
            _unsungRun = new Run { Foreground = new SolidColorBrush(unsungColor) };

            host.Inlines.Clear();
            host.Inlines.Add(_sungRun);
            host.Inlines.Add(_unsungRun);
        }

        /// <summary>已唱部分的高亮色。</summary>
        public Color SungColor
        {
            get => ((SolidColorBrush)_sungRun.Foreground).Color;
            set => _sungRun.Foreground = new SolidColorBrush(value);
        }

        /// <summary>未唱部分的颜色。</summary>
        public Color UnsungColor
        {
            get => ((SolidColorBrush)_unsungRun.Foreground).Color;
            set => _unsungRun.Foreground = new SolidColorBrush(value);
        }

        /// <summary>
        /// 更新显示内容。
        /// <paramref name="sung"/> 为已唱文本，<paramref name="unsung"/> 为未唱文本，
        /// 两者拼起来是完整的一行歌词。
        /// </summary>
        public void Update(string sung, string unsung)
        {
            string text = sung + unsung;

            // 文字没变就不动 Inline：避免每帧重新布局。
            if (!string.Equals(text, _currentText, StringComparison.Ordinal))
            {
                _currentText = text;
                _sungRun.Text = sung;
                _unsungRun.Text = unsung;
            }
        }

        /// <summary>把整行文字一次性写入（未唱段），用于切行时的初始状态。</summary>
        public void SetWholeText(string text)
        {
            _currentText = text;
            _sungRun.Text = string.Empty;
            _unsungRun.Text = text;
        }

        /// <summary>清空内容。</summary>
        public void Clear()
        {
            _currentText = string.Empty;
            _sungRun.Text = string.Empty;
            _unsungRun.Text = string.Empty;
        }
    }
}
