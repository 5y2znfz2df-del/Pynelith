using System;
using System.Runtime.InteropServices;
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
    /// 性能：复用 DIB 段与 DIB DC，每帧仅一次内存拷贝。
    /// </summary>
    internal sealed class ScreenSampler : IDisposable
    {
        private IntPtr _screenDc;
        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _oldDib;
        private IntPtr _bits;

        private WriteableBitmap? _bitmap;
        private int _width;
        private int _height;
        private bool _disposed;

        // 遮罩填充的复用缓冲：按需扩容，避免每帧分配
        private int[]? _maskPixels;
        private int[]? _maskLeft;
        private int[]? _maskRight;
        private int[]? _maskTop;
        private int[]? _maskBottom;

        /// <summary>可直接作为 ShaderEffect 采样器输入的画刷（图像随采样更新）。</summary>
        public ImageBrush Brush { get; } = new ImageBrush { Stretch = Stretch.Fill };

        /// <summary>
        /// 抓取屏幕物理像素区域。坐标须为屏幕设备像素（与 GetWindowRect 一致）。
        /// <paramref name="mask"/> 为抓屏图内的遮罩矩形（设备像素）：该区域会被四周像素
        /// 平滑填充，用于「窗口未被排除出捕获」时抹掉玻璃自身，避免自采样反馈。
        /// 返回 false 表示抓取失败（保持上一帧内容）。
        /// </summary>
        public bool Capture(int screenX, int screenY, int width, int height, Int32Rect mask)
        {
            if (_disposed || width <= 0 || height <= 0)
            {
                return false;
            }

            if (!EnsureBuffers(width, height))
            {
                return false;
            }

            if (_screenDc == IntPtr.Zero)
            {
                _screenDc = NativeMethods.GetDC(IntPtr.Zero);
                if (_screenDc == IntPtr.Zero)
                {
                    return false;
                }
            }

            // CAPTUREBLT 让分层窗口参与合成；抓屏排斥由 WDA_EXCLUDEFROMCAPTURE 控制。
            if (!NativeMethods.BitBlt(_memDc, 0, 0, width, height, _screenDc, screenX, screenY,
                                      NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return false;
            }

            if (mask.Width > 0 && mask.Height > 0)
            {
                FillMaskedRegion(width, height, mask);
            }

            int stride = width * 4;
            _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), _bits, stride * height, stride);
            return true;
        }

        /// <summary>
        /// 用遮罩四周的像素做反距离插值填充遮罩区域：
        /// 横向接左右邻居、纵向接上下邻居，4 个方向按距离加权。
        /// 玻璃内部本来就是染色画面，这块填充只作为折射的「内部背景」，
        /// 边缘折射仍然采样遮罩外的真实桌面像素。
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

            if (_maskLeft == null || _maskLeft.Length < spanY)
            {
                _maskLeft = new int[spanY];
                _maskRight = new int[spanY];
            }

            if (_maskTop == null || _maskTop.Length < spanX)
            {
                _maskTop = new int[spanX];
                _maskBottom = new int[spanX];
            }

            int stride = width * 4;
            int leftX = Math.Max(0, x0 - 1);
            int rightX = Math.Min(width - 1, x1);

            for (int y = 0; y < spanY; y++)
            {
                IntPtr row = IntPtr.Add(_bits, (y0 + y) * stride);
                _maskLeft![y] = Marshal.ReadInt32(row, leftX * 4);
                _maskRight![y] = Marshal.ReadInt32(row, rightX * 4);
            }

            int topY = Math.Max(0, y0 - 1);
            int bottomY = Math.Min(height - 1, y1);
            for (int x = 0; x < spanX; x++)
            {
                _maskTop![x] = Marshal.ReadInt32(_bits, topY * stride + (x0 + x) * 4);
                _maskBottom![x] = Marshal.ReadInt32(_bits, bottomY * stride + (x0 + x) * 4);
            }

            for (int y = 0; y < spanY; y++)
            {
                // 反距离权重：到哪条边越近，就越信那条边的颜色
                double wyTop = 1.0 / (y + 1.0);
                double wyBottom = 1.0 / (spanY - y);

                int left = _maskLeft![y];
                int right = _maskRight![y];
                int rowStart = y * spanX;

                for (int x = 0; x < spanX; x++)
                {
                    double wxLeft = 1.0 / (x + 1.0);
                    double wxRight = 1.0 / (spanX - x);
                    double total = wxLeft + wxRight + wyTop + wyBottom;

                    int top = _maskTop![x];
                    int bottom = _maskBottom![x];

                    int blue = (int)Math.Round(
                        ((left & 0xFF) * wxLeft + (right & 0xFF) * wxRight
                         + (top & 0xFF) * wyTop + (bottom & 0xFF) * wyBottom) / total);
                    int green = (int)Math.Round(
                        (((left >> 8) & 0xFF) * wxLeft + ((right >> 8) & 0xFF) * wxRight
                         + ((top >> 8) & 0xFF) * wyTop + ((bottom >> 8) & 0xFF) * wyBottom) / total);
                    int red = (int)Math.Round(
                        (((left >> 16) & 0xFF) * wxLeft + ((right >> 16) & 0xFF) * wxRight
                         + ((top >> 16) & 0xFF) * wyTop + ((bottom >> 16) & 0xFF) * wyBottom) / total);

                    _maskPixels[rowStart + x] = unchecked((int)0xFF000000)
                        | (Math.Clamp(red, 0, 255) << 16)
                        | (Math.Clamp(green, 0, 255) << 8)
                        | Math.Clamp(blue, 0, 255);
                }
            }

            for (int y = 0; y < spanY; y++)
            {
                Marshal.Copy(_maskPixels, y * spanX,
                    IntPtr.Add(_bits, (y0 + y) * stride + x0 * 4), spanX);
            }
        }

        private bool EnsureBuffers(int width, int height)
        {
            if (_bitmap != null && _width == width && _height == height)
            {
                return true;
            }

            ReleaseBuffers();

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

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return false;
            }

            _memDc = NativeMethods.CreateCompatibleDC(screenDc);
            _dib = NativeMethods.CreateDIBSection(screenDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

            if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                ReleaseBuffers();
                return false;
            }

            _oldDib = NativeMethods.SelectObject(_memDc, _dib);

            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _width = width;
            _height = height;
            Brush.ImageSource = _bitmap;
            return true;
        }

        private void ReleaseBuffers()
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

            _bitmap = null;
            _width = _height = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }

            ReleaseBuffers();
        }
    }
}
