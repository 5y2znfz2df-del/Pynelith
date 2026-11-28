using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Pynelith.Island.Services.Motion
{
    /// <summary>某一帧的胶囊形变参数（窗口坐标系，单位 DIP）。</summary>
    public readonly struct MotionFrame
    {
        public MotionFrame(double time, double x, double y, double width, double height, double radius)
        {
            Time = time;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Radius = radius;
        }

        /// <summary>相对录制开始的秒数。</summary>
        public double Time { get; }

        /// <summary>胶囊左上角 X。</summary>
        public double X { get; }

        /// <summary>胶囊左上角 Y。</summary>
        public double Y { get; }

        public double Width { get; }

        public double Height { get; }

        /// <summary>圆角半径（曲率）。</summary>
        public double Radius { get; }
    }

    /// <summary>
    /// 形变动画录制器：把每一帧的胶囊位置 / 尺寸 / 圆角记下来，
    /// 供宣传片工作流导出为 Lottie（After Effects 可直接读）。
    ///
    /// 只在用户主动开始录制时占用内存：默认上限 8 秒 / 600 帧，
    /// 达到上限自动收尾并触发 <see cref="Completed"/>。
    /// </summary>
    public sealed class MotionTrace
    {
        private readonly List<MotionFrame> _frames = new List<MotionFrame>(256);
        private readonly Stopwatch _clock = new Stopwatch();

        /// <summary>录制上限（秒）。</summary>
        public double MaxSeconds { get; set; } = 8;

        /// <summary>帧数上限。</summary>
        public int MaxFrames { get; set; } = 600;

        /// <summary>是否正在录制。</summary>
        public bool IsRecording { get; private set; }

        /// <summary>已录制的帧数。</summary>
        public int FrameCount => _frames.Count;

        /// <summary>自动收尾时触发（达到时间 / 帧数上限）。</summary>
        public event EventHandler? Completed;

        public void Start()
        {
            _frames.Clear();
            _clock.Restart();
            IsRecording = true;
        }

        /// <summary>丢弃当前录制。</summary>
        public void Cancel()
        {
            IsRecording = false;
            _frames.Clear();
            _clock.Reset();
        }

        /// <summary>
        /// 记录一帧。返回 false 表示本次录制已结束（达到上限）。
        /// </summary>
        public bool Capture(double x, double y, double width, double height, double radius)
        {
            if (!IsRecording)
            {
                return false;
            }

            double time = _clock.Elapsed.TotalSeconds;
            _frames.Add(new MotionFrame(time, x, y, width, height, radius));

            if (time >= MaxSeconds || _frames.Count >= MaxFrames)
            {
                IsRecording = false;
                _clock.Stop();
                Completed?.Invoke(this, EventArgs.Empty);
                return false;
            }

            return true;
        }

        /// <summary>结束录制并取走帧序列（取走后录制器清空）。</summary>
        public IReadOnlyList<MotionFrame> Stop()
        {
            IsRecording = false;
            _clock.Stop();
            MotionFrame[] frames = _frames.ToArray();
            _frames.Clear();
            return frames;
        }

        /// <summary>
        /// 导出为 Lottie JSON 文件，返回写出的路径。
        /// </summary>
        public static string Export(
            IReadOnlyList<MotionFrame> frames,
            double compositionWidth,
            double compositionHeight,
            int frameRate,
            string directory)
        {
            if (frames.Count == 0)
            {
                throw new ArgumentException("没有可导出的帧", nameof(frames));
            }

            if (frameRate < 1)
            {
                frameRate = 60;
            }

            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"island-motion-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            File.WriteAllText(
                path,
                LottieWriter.Build(frames, compositionWidth, compositionHeight, frameRate),
                new UTF8Encoding(false));

            return path;
        }
    }

    /// <summary>
    /// 把形变帧序列写成 Lottie 5.9 的 JSON。
    /// 只用一个圆角矩形图层：位置、尺寸、圆角三条属性做关键帧，
    /// 尺寸与位置直接取录制值，After Effects / LottieFiles 打开即所见即所得。
    /// </summary>
    public static class LottieWriter
    {
        public static string Build(
            IReadOnlyList<MotionFrame> frames,
            double compositionWidth,
            double compositionHeight,
            int frameRate)
        {
            int fps = Math.Max(1, frameRate);
            var keys = BuildKeys(frames, fps);

            int lastFrame = keys[keys.Count - 1].Frame;
            int op = lastFrame + 1;

            var sb = new StringBuilder(1024 + keys.Count * 96);
            sb.Append("{\"v\":\"5.9.0\",\"fr\":").Append(fps)
              .Append(",\"ip\":0,\"op\":").Append(op)
              .Append(",\"w\":").Append(Num(Math.Max(1, compositionWidth)))
              .Append(",\"h\":").Append(Num(Math.Max(1, compositionHeight)))
              .Append(",\"nm\":\"Pynelith Island\",\"ddd\":0,\"assets\":[],\"layers\":[")
              .Append("{\"ddd\":0,\"ind\":1,\"ty\":4,\"nm\":\"Capsule\",\"sr\":1,")
              .Append("\"ks\":{\"o\":{\"a\":0,\"k\":100},\"r\":{\"a\":0,\"k\":0},");

            AppendProperty(
                sb,
                "p",
                keys,
                k => new[] { k.CenterX, k.CenterY });

            sb.Append(",\"a\":{\"a\":0,\"k\":[0,0,0]},\"s\":{\"a\":0,\"k\":[100,100,100]}},")
              .Append("\"ao\":0,\"shapes\":[{\"ty\":\"gr\",\"nm\":\"Capsule\",\"it\":[");

            sb.Append("{\"ty\":\"rc\",\"d\":1,");
            AppendProperty(sb, "s", keys, k => new[] { k.Width, k.Height });
            sb.Append(",\"p\":{\"a\":0,\"k\":[0,0]},");
            AppendProperty(sb, "r", keys, k => new[] { k.Radius });
            sb.Append('}');

            sb.Append(",{\"ty\":\"fl\",\"c\":{\"a\":0,\"k\":[0.09,0.09,0.11,1]},\"o\":{\"a\":0,\"k\":100},\"r\":1},")
              .Append("{\"ty\":\"tr\",\"p\":{\"a\":0,\"k\":[0,0]},\"a\":{\"a\":0,\"k\":[0,0]},")
              .Append("\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}")
              .Append("]}]")
              .Append(",\"ip\":0,\"op\":").Append(op).Append(",\"st\":0,\"bm\":0}")
              .Append("],\"markers\":[]}");

            return sb.ToString();
        }

        private readonly struct Key
        {
            public Key(int frame, double centerX, double centerY, double width, double height, double radius)
            {
                Frame = frame;
                CenterX = centerX;
                CenterY = centerY;
                Width = width;
                Height = height;
                Radius = radius;
            }

            public int Frame { get; }

            public double CenterX { get; }

            public double CenterY { get; }

            public double Width { get; }

            public double Height { get; }

            public double Radius { get; }
        }

        /// <summary>把帧序列换算成整数帧号，并去掉同一帧号上的重复项（保留最后一帧）。</summary>
        private static List<Key> BuildKeys(IReadOnlyList<MotionFrame> frames, int fps)
        {
            var keys = new List<Key>(frames.Count);

            foreach (MotionFrame f in frames)
            {
                int frame = (int)Math.Round(f.Time * fps, MidpointRounding.AwayFromZero);
                var key = new Key(
                    frame,
                    f.X + f.Width * 0.5,
                    f.Y + f.Height * 0.5,
                    f.Width,
                    f.Height,
                    f.Radius);

                if (keys.Count > 0 && keys[keys.Count - 1].Frame == frame)
                {
                    keys[keys.Count - 1] = key;
                }
                else
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static void AppendProperty(
            StringBuilder sb,
            string name,
            List<Key> keys,
            Func<Key, double[]> selector)
        {
            sb.Append('"').Append(name).Append("\":{\"a\":1,\"k\":[");

            for (int i = 0; i < keys.Count; i++)
            {
                double[] value = selector(keys[i]);
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"t\":").Append(keys[i].Frame).Append(",\"s\":[");
                for (int v = 0; v < value.Length; v++)
                {
                    if (v > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(Num(value[v]));
                }

                sb.Append(']');

                // 最后一帧是停留帧，Lottie 里不需要缓动手柄
                if (i < keys.Count - 1)
                {
                    sb.Append(",\"i\":{\"x\":").Append(Handles(0.667, value.Length))
                      .Append(",\"y\":").Append(Handles(1, value.Length))
                      .Append("},\"o\":{\"x\":").Append(Handles(0.333, value.Length))
                      .Append(",\"y\":").Append(Handles(0, value.Length))
                      .Append('}');
                }

                sb.Append('}');
            }

            sb.Append("]}");
        }

        private static string Handles(double value, int dimensions)
        {
            var sb = new StringBuilder(dimensions * 8 + 2);
            sb.Append('[');
            for (int i = 0; i < dimensions; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Num(value));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static string Num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "0";
            }

            return Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        }
    }
}
