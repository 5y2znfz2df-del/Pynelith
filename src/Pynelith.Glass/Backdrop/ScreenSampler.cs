using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pynelith.Glass.Interop;
using SkiaSharp;

namespace Pynelith.Glass.Backdrop
{
    /// <summary>
    /// 背景采样器：GDI BitBlt 抓取屏幕区域，再用 Skia 做降采样高斯模糊，
    /// 生成液态玻璃所需的预模糊背景贴图。
    ///
    /// 对应 WinIsland 的 backdrop.blurred：那边用 HostBackdropBrush + D2D GaussianBlur，
    /// 这里用 Skia 的 ImageFilter 做等价处理（结果同样是"已模糊的背景位图"）。
    ///
    /// 性能：复用 DIB 段与输出位图，模糊在降采样后的低分辨率上进行；
    /// WPF 负责把结果拉到元素尺寸（等价 WinIsland 的 blur_scale 采样）。
    /// </summary>
    internal sealed class ScreenSampler : IDisposable
    {
        /// <summary>降采样倍数，越大越省 CPU、越柔和。</summary>
        private const int Downsample = 2;

        private IntPtr _screenDc;
        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _oldDib;
        private IntPtr _bits;

        private WriteableBitmap? _bitmap;
        private SKBitmap? _skTarget;
        private SKCanvas? _skCanvas;
        private SKImageFilter? _blurFilter;
        private float _sigma = 3f;

        private int _width;
        private int _height;
        private bool _disposed;

        /// <summary>可直接作为 ShaderEffect 采样器输入的画刷（图像随采样更新）。</summary>
        public ImageBrush Brush { get; } = new ImageBrush { Stretch = Stretch.Fill };

        /// <summary>模糊强度（目标像素的 sigma），改变后下一帧生效。</summary>
        public float BlurSigma
        {
            get => _sigma;
            set
            {
                float v = Math.Max(0f, value);
                if (Math.Abs(v - _sigma) < 0.01f)
                {
                    return;
                }

                _sigma = v;
                _blurFilter?.Dispose();
                _blurFilter = null;
            }
        }

        /// <summary>
        /// 抓取屏幕物理像素区域并生成模糊背景。坐标须为屏幕设备像素。
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

            if (!NativeMethods.BitBlt(_memDc, 0, 0, width, height, _screenDc, screenX, screenY,
                                      NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return false;
            }

            BlurToTarget(width, height);

            int stride = _width * 4;
            _bitmap!.WritePixels(new Int32Rect(0, 0, _width, _height), _skTarget!.GetPixels(), stride * _height, stride);
            return true;
        }

        /// <summary>用 Skia 把 DIB 内容降采样并高斯模糊到目标位图。</summary>
        private void BlurToTarget(int sourceWidth, int sourceHeight)
        {
            var info = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var source = new SKBitmap();
            if (!source.InstallPixels(info, _bits, sourceWidth * 4))
            {
                return;
            }

            using var image = SKImage.FromBitmap(source);
            _blurFilter ??= SKImageFilter.CreateBlur(_sigma, _sigma);

            using var paint = new SKPaint { ImageFilter = _blurFilter, IsAntialias = true };
            _skCanvas!.Clear(SKColors.Black);
            _skCanvas.DrawImage(
                image,
                SKRect.Create(_width, _height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                paint);
            _skCanvas.Flush();
        }

        private bool EnsureBuffers(int width, int height)
        {
            if (_bitmap != null && _width == Math.Max(1, width / Downsample) && _height == Math.Max(1, height / Downsample))
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

            _width = Math.Max(1, width / Downsample);
            _height = Math.Max(1, height / Downsample);

            _skTarget = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            _skCanvas = new SKCanvas(_skTarget);

            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            Brush.ImageSource = _bitmap;
            return true;
        }

        private void ReleaseBuffers()
        {
            _skCanvas?.Dispose();
            _skCanvas = null;

            _skTarget?.Dispose();
            _skTarget = null;

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

            _blurFilter?.Dispose();
            _blurFilter = null;
        }
    }
}
