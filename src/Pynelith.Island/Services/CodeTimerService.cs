using System;
using System.Diagnostics;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 代码计时器：Stopwatch 的薄封装，只负责"开始 / 暂停 / 重置 + 格式化"。
    /// 显示刷新交给调用方（岛里复用 1 秒的时钟节拍），本类不自带定时器。
    /// </summary>
    public sealed class CodeTimerService
    {
        private readonly Stopwatch _watch = new Stopwatch();

        /// <summary>运行状态或累计时间发生变化。</summary>
        public event EventHandler? Changed;

        /// <summary>是否正在计时。</summary>
        public bool IsRunning => _watch.IsRunning;

        /// <summary>累计时长（暂停期间不增长）。</summary>
        public TimeSpan Elapsed => _watch.Elapsed;

        /// <summary>开始 / 继续计时。</summary>
        public void Start()
        {
            if (_watch.IsRunning)
            {
                return;
            }

            _watch.Start();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>暂停计时（累计时间保留）。</summary>
        public void Pause()
        {
            if (!_watch.IsRunning)
            {
                return;
            }

            _watch.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>开始 / 暂停切换。</summary>
        public void Toggle()
        {
            if (_watch.IsRunning)
            {
                Pause();
            }
            else
            {
                Start();
            }
        }

        /// <summary>归零并停止。</summary>
        public void Reset()
        {
            if (_watch.ElapsedTicks == 0 && !_watch.IsRunning)
            {
                return;
            }

            _watch.Reset();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 显示文本：一小时以内为 <c>mm:ss.f</c>，超过一小时后为 <c>h:mm:ss</c>。
        /// </summary>
        public static string Format(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            return elapsed.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds)
                : string.Format("{0:00}:{1:00}.{2}", elapsed.Minutes, elapsed.Seconds, elapsed.Milliseconds / 100);
        }

        /// <summary>当前累计时长的显示文本。</summary>
        public string DisplayText => Format(_watch.Elapsed);
    }
}
