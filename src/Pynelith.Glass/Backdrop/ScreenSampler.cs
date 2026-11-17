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
        private int[]? _maskTop;
        private int[]? _maskBottom;
        private int[]? _maskTemp;

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
