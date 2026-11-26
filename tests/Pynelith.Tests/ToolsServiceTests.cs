using System;
using System.Threading;
using Pynelith.Island.Services;
using Xunit;

namespace Pynelith.Tests
{
    /// <summary>剪贴板历史：去重、置顶、上限裁剪、清空。</summary>
    public sealed class ClipboardHistoryTests
    {
        [Fact]
        public void Add_NewestFirst()
        {
            var history = new ClipboardHistory();
            history.Add("a");
            history.Add("b");

            Assert.Equal(2, history.Items.Count);
            Assert.Equal("b", history.Items[0].Text);
            Assert.Equal("a", history.Items[1].Text);
        }

        [Fact]
        public void Add_DuplicateMovesToTop_WithoutDuplicating()
        {
            var history = new ClipboardHistory();
            history.Add("a");
            history.Add("b");
            history.Add("c");

            Assert.True(history.Add("a"));

            Assert.Equal(3, history.Items.Count);
            Assert.Equal("a", history.Items[0].Text);
        }

        [Fact]
        public void Add_SameAsCurrent_IsIgnored()
        {
            var history = new ClipboardHistory();
            history.Add("a");

            Assert.False(history.Add("a"));
            Assert.Single(history.Items);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n\t")]
        public void Add_BlankIsIgnored(string? text)
        {
            var history = new ClipboardHistory();

            Assert.False(history.Add(text));
            Assert.Empty(history.Items);
        }

        [Fact]
        public void Add_TrimsToLimit_DroppingOldest()
        {
            var history = new ClipboardHistory { Limit = 3 };
            history.Add("a");
            history.Add("b");
            history.Add("c");
            history.Add("d");

            Assert.Equal(3, history.Items.Count);
            Assert.Equal("d", history.Items[0].Text);
            Assert.Equal("b", history.Items[2].Text);
        }

        [Fact]
        public void Limit_LoweredAfterwards_TrimsImmediately()
        {
            var history = new ClipboardHistory();
            for (int i = 0; i < 10; i++)
            {
                history.Add("item-" + i);
            }

            history.Limit = 4;

            Assert.Equal(4, history.Items.Count);
            Assert.Equal("item-9", history.Items[0].Text);
        }

        [Fact]
        public void Add_TruncatesOverlongText()
        {
            var history = new ClipboardHistory();
            string huge = new string('x', ClipboardHistory.MaxTextLength + 100);

            history.Add(huge);

            Assert.Equal(ClipboardHistory.MaxTextLength, history.Items[0].Text.Length);
        }

        [Fact]
        public void Clear_EmptiesHistory_AndRaisesChanged()
        {
            var history = new ClipboardHistory();
            history.Add("a");
            int changed = 0;
            history.Changed += (_, _) => changed++;

            history.Clear();

            Assert.Empty(history.Items);
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Preview_CollapsesLineBreaks()
        {
            var history = new ClipboardHistory();
            history.Add("line1\r\nline2");

            Assert.Equal("line1 line2", history.Items[0].Preview);
        }
    }

    /// <summary>代码计时器：开始 / 暂停 / 重置与文本格式。</summary>
    public sealed class CodeTimerServiceTests
    {
        [Fact]
        public void StartThenPause_AccumulatesTime()
        {
            var timer = new CodeTimerService();

            timer.Start();
            Thread.Sleep(60);
            timer.Pause();

            Assert.False(timer.IsRunning);
            Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(40), "累计时长应接近实际等待时间");
        }

        [Fact]
        public void Paused_DoesNotKeepCounting()
        {
            var timer = new CodeTimerService();
            timer.Start();
            Thread.Sleep(40);
            timer.Pause();

            TimeSpan first = timer.Elapsed;
            Thread.Sleep(50);

            Assert.Equal(first, timer.Elapsed);
        }

        [Fact]
        public void Toggle_SwitchesBetweenRunAndPause()
        {
            var timer = new CodeTimerService();

            timer.Toggle();
            Assert.True(timer.IsRunning);

            timer.Toggle();
            Assert.False(timer.IsRunning);
        }

        [Fact]
        public void Reset_ZeroesAndStops()
        {
            var timer = new CodeTimerService();
            timer.Start();
            Thread.Sleep(30);

            timer.Reset();

            Assert.Equal(TimeSpan.Zero, timer.Elapsed);
            Assert.False(timer.IsRunning);
        }

        [Theory]
        [InlineData(0.0, "00:00.0")]
        [InlineData(9.9, "00:09.9")]
        [InlineData(75.4, "01:15.4")]
        [InlineData(599.0, "09:59.0")]
        public void Format_UnderOneHour(double seconds, string expected)
        {
            Assert.Equal(expected, CodeTimerService.Format(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void Format_OverOneHour_SwitchesShape()
        {
            Assert.Equal("1:02:03", CodeTimerService.Format(new TimeSpan(1, 2, 3)));
        }

        [Fact]
        public void Format_NegativeIsClampedToZero()
        {
            Assert.Equal("00:00.0", CodeTimerService.Format(TimeSpan.FromSeconds(-5)));
        }
    }
}
