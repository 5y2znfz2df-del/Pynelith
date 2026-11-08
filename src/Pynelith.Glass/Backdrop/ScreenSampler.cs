using System;
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

        /// <summary>可直接作为 ShaderEffect 采样器输入的画刷（图像随采样更新）。</summary>
        public ImageBrush Brush { get; } = new ImageBrush { Stretch = Stretch.Fill };

        /// <summary>
        /// 抓取屏幕物理像素区域。坐标须为屏幕设备像素（与 GetWindowRect 一致）。
        /// 返回 false 表示抓取失败（保持上一帧内容）。
        /// </summary>
        public bool Capture(int screenX, int screenY, int width, int height)
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

            // CAPTUREBLT 让分层窗口参与合成；本窗口已被 WDA_EXCLUDEFROMCAPTURE 排除。
            if (!NativeMethods.BitBlt(_memDc, 0, 0, width, height, _screenDc, screenX, screenY,
                                      NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return false;
            }

            int stride = width * 4;
            _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), _bits, stride * height, stride);
            return true;
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
