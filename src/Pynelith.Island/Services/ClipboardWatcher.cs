using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 剪贴板监听：自建一个 message-only 窗口接收 <c>WM_CLIPBOARDUPDATE</c>，
    /// 事件驱动（不是轮询），系统剪贴板没变化时零开销。
    /// 必须在 UI 线程（STA）创建与释放。
    /// </summary>
    public sealed class ClipboardWatcher : IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private static readonly IntPtr HwndMessage = new IntPtr(-3);
        private const int ReadAttempts = 4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private readonly HwndSource _source;
        private bool _disposed;

        /// <summary>采集到非空文本时触发（UI 线程）。</summary>
        public event EventHandler<string>? TextCaptured;

        public ClipboardWatcher()
        {
            var parameters = new HwndSourceParameters("Pynelith.ClipboardWatcher")
            {
                WindowStyle = WS_POPUP,
                ParentWindow = HwndMessage,
                Width = 0,
                Height = 0,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            // 注册失败（极少数会话限制）不抛异常：工具面板其余功能照常可用
            AddClipboardFormatListener(_source.Handle);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                TryReadText();
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 读取文本。剪贴板随时可能被其它进程占用（OpenClipboard 失败），
        /// 因此最多重试 <see cref="ReadAttempts"/> 次，每次间隔 15ms。
        /// </summary>
        private void TryReadText()
        {
            for (int attempt = 0; attempt < ReadAttempts; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsText())
                    {
                        return;
                    }

                    string text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        TextCaptured?.Invoke(this, text);
                    }

                    return;
                }
                catch (COMException)
                {
                    // 被其它进程锁住：稍等再试
                    Thread.Sleep(15);
                }
                catch (Exception)
                {
                    // 其它异常（剪贴板正在清空等）：放弃本次读取
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TextCaptured = null;

            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
