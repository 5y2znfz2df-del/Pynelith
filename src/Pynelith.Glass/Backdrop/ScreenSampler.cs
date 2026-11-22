using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pynelith.Glass.Interop;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// 屏幕背景采样器：GDI BitBlt 抓取指定屏幕区域，写入可复用的 WriteableBitmap。
    ///
    /// 这是液态玻璃折射的前提 —— 着色器要拿到窗口背后的真实像素。
    /// 注意：**不做任何模糊**（模糊是亚克力的特征，会让玻璃失去透亮感）。
    /// 抓取区域会比玻璃本身四周各外扩一段余量，供边缘折射采样外部像素。
    ///
    /// 线程模型（关键）：
    /// 抓屏是整个形变动画里最重的一步 —— BitBlt 会同步 DWM，耗时随桌面活动剧烈波动，
    /// 放在 UI 线程上就会把形变动画的某一帧拉长到 35ms 以上。
    /// 因此采样整体搬到独立的工作线程：
    /// - 工作线程：BitBlt + 遮罩填充 + 拷进托管缓冲，全程不碰任何 WPF 对象；
    /// - UI 线程：只把已完成的帧拷进 WriteableBitmap（一次 memcpy）并触发合成。
    /// 抓屏再慢也只会让画面晚一帧更新，不会拉长 UI 线程上的动画帧。
    ///
    /// 请求合并：工作线程只处理最新的一次请求，抓屏慢时自动降帧而不是排队堆积。
    /// </summary>
    internal sealed class ScreenSampler : IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<byte[]> _freeBuffers = new List<byte[]>(4);
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);

        private Thread? _worker;
        private bool _disposed;

        // ---- 采样请求（UI 线程写入，工作线程读取；新请求覆盖旧请求） ----
        private bool _hasRequest;
        private int _reqX;
        private int _reqY;
        private int _reqWidth;
        private int _reqHeight;
        private Int32Rect _reqMask;

        // ---- 帧交接（工作线程写入，UI 线程取走） ----
        private byte[]? _readyBuffer;
        private int _readyWidth;
        private int _readyHeight;
        private bool _hasReady;

        // ---- GDI 资源：仅工作线程使用 ----
        private IntPtr _screenDc;
        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _oldDib;
        private IntPtr _bits;
        private int _dibWidth;
        private int _dibHeight;

        // ---- 位图与画刷：仅 UI 线程使用 ----
        private WriteableBitmap? _bitmap;
        private int _bitmapWidth;
        private int _bitmapHeight;

        // 遮罩填充的复用缓冲：按需扩容，避免每帧分配
        private int[]? _maskPixels;
        private int[]? _maskTop;
        private int[]? _maskBottom;
        private int[]? _maskTemp;

        /// <summary>可直接作为 ShaderEffect 采样器输入的画刷（图像随采样更新）。</summary>
        public ImageBrush Brush { get; } = new ImageBrush { Stretch = Stretch.Fill };

        public ScreenSampler()
        {
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "Pynelith.Glass.Sampler",
                // 采样线程优先级略低于 UI：宁可晚一帧出图，也不要抢形变动画的时间片。
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        /// <summary>
        /// 请求一次抓屏（UI 线程调用）。
        /// 坐标须为屏幕设备像素（与 GetWindowRect 一致）。
        /// <paramref name="mask"/> 为抓屏图内的遮罩矩形（设备像素）：该区域会被四周像素
        /// 平滑填充，用于「窗口未被排除出捕获」时抹掉玻璃自身，避免自采样反馈。
        ///
        /// 请求会被合并：抓屏慢时自动降帧，不会堆积成延迟越来越大的队列。
        /// </summary>
        public void Request(int screenX, int screenY, int width, int height, Int32Rect mask)
        {
            if (_disposed || width <= 0 || height <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _reqX = screenX;
                _reqY = screenY;
                _reqWidth = width;
                _reqHeight = height;
                _reqMask = mask;
                _hasRequest = true;
            }

            _wake.Set();
        }

        /// <summary>
        /// 把工作线程已完成的帧拷进位图（UI 线程调用）。
        /// 返回 true 表示这一拍有新帧进入着色器。
        /// </summary>
        public bool TryUpload()
        {
            byte[]? buffer;
            int width;
            int height;

            lock (_gate)
            {
                if (!_hasReady)
                {
                    return false;
                }

                _hasReady = false;
                buffer = _readyBuffer;
                _readyBuffer = null;
                width = _readyWidth;
                height = _readyHeight;
            }

            if (buffer == null)
            {
                return false;
            }

            try
            {
                EnsureBitmap(width, height);
                if (_bitmap == null)
                {
                    return false;
                }

                // 走 IntPtr 重载：bufferSize 与 stride 的单位明确是字节，
                // 不会出现"传的是像素数还是字节数"的歧义。
                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    _bitmap.WritePixels(
                        new Int32Rect(0, 0, width, height),
                        handle.AddrOfPinnedObject(),
                        width * height * 4,
                        width * 4);
                }
                finally
                {
                    handle.Free();
                }

                return true;
            }
            finally
            {
                RecycleBuffer(buffer);
            }
        }

        #region 工作线程

        private void WorkerLoop()
        {
            try
            {
                while (true)
                {
                    _wake.Wait();
                    if (_disposed)
                    {
                        break;
                    }

                    // 一轮唤醒可能对应多次请求（期间又来了新请求），全部处理掉。
                    while (true)
                    {
                        // Reset 放在读取请求之前：避免"刚 Set 就被 Reset"把唤醒吃掉。
                        _wake.Reset();

                        int x;
                        int y;
                        int width;
                        int height;
                        Int32Rect mask;

                        lock (_gate)
                        {
                            if (!_hasRequest)
                            {
                                break;
                            }

                            _hasRequest = false;
                            x = _reqX;
                            y = _reqY;
                            width = _reqWidth;
                            height = _reqHeight;
                            mask = _reqMask;
                        }

                        Capture(x, y, width, height, mask);
                    }
                }
            }
            finally
            {
                ReleaseDib();
                if (_screenDc != IntPtr.Zero)
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                    _screenDc = IntPtr.Zero;
                }
            }
        }

        private void Capture(int screenX, int screenY, int width, int height, Int32Rect mask)
        {
            if (!EnsureDib(width, height))
            {
                return;
            }

            // CAPTUREBLT 让分层窗口参与合成；抓屏排斥由 WDA_EXCLUDEFROMCAPTURE 控制。
            if (!NativeMethods.BitBlt(_memDc, 0, 0, width, height, _screenDc, screenX, screenY,
                                      NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return;
            }

            if (mask.Width > 0 && mask.Height > 0)
            {
                FillMaskedRegion(width, height, mask);
            }

            int stride = width * 4;
            int size = stride * height;

            byte[] buffer = RentBuffer(size);
            Marshal.Copy(_bits, buffer, 0, size);

            byte[]? dropped = null;
            lock (_gate)
            {
                dropped = _readyBuffer;
                _readyBuffer = buffer;
                _readyWidth = width;
                _readyHeight = height;
                _hasReady = true;
            }

            // UI 线程还没来得及取走的旧帧直接丢弃：宁可少一帧，也不要延迟累积。
            if (dropped != null)
            {
                RecycleBuffer(dropped);
            }
        }

        /// <summary>从缓冲池取一块足够大的托管缓冲（工作线程）。</summary>
        private byte[] RentBuffer(int size)
        {
            lock (_gate)
            {
                for (int i = _freeBuffers.Count - 1; i >= 0; i--)
                {
                    if (_freeBuffers[i].Length >= size)
                    {
                        byte[] pooled = _freeBuffers[i];
                        _freeBuffers.RemoveAt(i);
                        return pooled;
                    }
                }
            }

            return new byte[size];
        }

        /// <summary>把用完的缓冲放回池中。</summary>
        private void RecycleBuffer(byte[] buffer)
        {
            lock (_gate)
            {
                if (_freeBuffers.Count < 4)
                {
                    _freeBuffers.Add(buffer);
                }
            }
        }

        private bool EnsureDib(int width, int height)
        {
            if (_memDc != IntPtr.Zero && _dibWidth == width && _dibHeight == height)
            {
                return true;
            }

            ReleaseDib();

            var bmi = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = 40,
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)NativeMethods.BI_RGB,
                biSizeImage = width * height * 4,
            };

            if (_screenDc == IntPtr.Zero)
            {
                _screenDc = NativeMethods.GetDC(IntPtr.Zero);
                if (_screenDc == IntPtr.Zero)
                {
                    return false;
                }
            }

            _memDc = NativeMethods.CreateCompatibleDC(_screenDc);
            _dib = NativeMethods.CreateDIBSection(_screenDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);

            if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                ReleaseDib();
                return false;
            }

            _oldDib = NativeMethods.SelectObject(_memDc, _dib);
            _dibWidth = width;
            _dibHeight = height;
            return true;
        }

        private void ReleaseDib()
        {
            if (_memDc != IntPtr.Zero && _oldDib != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memDc, _oldDib);
                _oldDib = IntPtr.Zero;
            }

            if (_dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_dib);
                _dib = IntPtr.Zero;
                _bits = IntPtr.Zero;
            }

            if (_memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }

            _dibWidth = 0;
            _dibHeight = 0;
        }

        #endregion

        #region UI 线程

        private void EnsureBitmap(int width, int height)
        {
            if (_bitmap != null && _bitmapWidth == width && _bitmapHeight == height)
            {
                return;
            }

            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _bitmapWidth = width;
            _bitmapHeight = height;
            Brush.ImageSource = _bitmap;
        }

        #endregion

        /// <summary>
        /// 用遮罩上下边框的像素做纵向插值填充：
        /// 保留每列的颜色变化（竖条纹、渐变会被正确延续），边缘折射才有细节可"掰"；
        /// 不做横向插值，避免把紧贴左右边框的文字拖进面板中间。
        /// </summary>
        private void FillMaskedRegion(int width, int height, Int32Rect mask)
        {
            int x0 = Math.Max(0, mask.X);
            int y0 = Math.Max(0, mask.Y);
            int x1 = Math.Min(width, mask.X + mask.Width);
            int y1 = Math.Min(height, mask.Y + mask.Height);

            int spanX = x1 - x0;
            int spanY = y1 - y0;
            if (spanX <= 0 || spanY <= 0)
            {
                return;
            }

            int count = spanX * spanY;
            if (_maskPixels == null || _maskPixels.Length < count)
            {
                _maskPixels = new int[count];
            }

            if (_maskTop == null || _maskTop.Length < spanX)
            {
                _maskTop = new int[spanX];
                _maskBottom = new int[spanX];
            }

            int stride = width * 4;

            int topY = Math.Max(0, y0 - 1);
            int bottomY = Math.Min(height - 1, y1);
            for (int x = 0; x < spanX; x++)
            {
                _maskTop![x] = Marshal.ReadInt32(_bits, topY * stride + (x0 + x) * 4);
                _maskBottom![x] = Marshal.ReadInt32(_bits, bottomY * stride + (x0 + x) * 4);
            }

            // 上下边框各做一次横向均值平滑（±6px）：
            // 文字/图标这类高频内容不会沿整列被"抽"成一条亮带、一路折射到面板中间，
            // 10px 以上的条纹与渐变仍能保留。
            if (_maskTemp == null || _maskTemp.Length < spanX)
            {
                _maskTemp = new int[spanX];
            }

            SmoothRow(_maskTop!, _maskTemp, spanX, 6);
            SmoothRow(_maskBottom!, _maskTemp, spanX, 6);

            // 逐列在「上边框像素 → 下边框像素」之间做纵向插值：
            // 竖条纹、纵向渐变这类背景能被正确延续，边缘折射才有可"掰"的细节；
            // 不做横向拉伸，避免把紧贴左右边框的文字/图标拖进面板中间。
            for (int y = 0; y < spanY; y++)
            {
                // 定点权重（0..256）：避免每像素 double 运算与 Math.Round
                int wBottom = (int)Math.Round((y + 1.0) * 256.0 / (spanY + 1.0));
                int wTop = 256 - wBottom;
                int rowStart = y * spanX;

                for (int x = 0; x < spanX; x++)
                {
                    int top = _maskTop![x];
                    int bottom = _maskBottom![x];

                    int blue = ((top & 0xFF) * wTop + (bottom & 0xFF) * wBottom) >> 8;
                    int green = (((top >> 8) & 0xFF) * wTop + ((bottom >> 8) & 0xFF) * wBottom) >> 8;
                    int red = (((top >> 16) & 0xFF) * wTop + ((bottom >> 16) & 0xFF) * wBottom) >> 8;

                    _maskPixels[rowStart + x] = unchecked((int)0xFF000000)
                        | (red << 16)
                        | (green << 8)
                        | blue;
                }
            }

            for (int y = 0; y < spanY; y++)
            {
                Marshal.Copy(_maskPixels, y * spanX,
                    IntPtr.Add(_bits, (y0 + y) * stride + x0 * 4), spanX);
            }
        }

        /// <summary>对一行像素做横向盒式平滑，抑制文字/图标被拉成整列亮带。</summary>
        private static void SmoothRow(int[] row, int[] scratch, int length, int radius)
        {
            if (length <= 1 || radius <= 0)
            {
                return;
            }

            for (int i = 0; i < length; i++)
            {
                int from = Math.Max(0, i - radius);
                int to = Math.Min(length - 1, i + radius);
                int count = to - from + 1;

                long blue = 0, green = 0, red = 0;
                for (int j = from; j <= to; j++)
                {
                    int color = row[j];
                    blue += color & 0xFF;
                    green += (color >> 8) & 0xFF;
                    red += (color >> 16) & 0xFF;
                }

                scratch[i] = unchecked((int)0xFF000000)
                    | ((int)(red / count) << 16)
                    | ((int)(green / count) << 8)
                    | (int)(blue / count);
            }

            Array.Copy(scratch, row, length);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            // 唤醒工作线程，让它走完 finally 释放 GDI 资源。
            _wake.Set();
            _worker?.Join(500);
            _worker = null;
        }
    }
}
