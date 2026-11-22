using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// 麦克风采集：后台收音，供唤醒识别与语音转写使用。
    ///
    /// 采集规格固定为 16kHz / 16bit / 单声道 —— Vosk 与主流 ASR 接口都只认这个格式，
    /// 统一在这里转换，避免各调用方各自重采样。
    /// </summary>
    public sealed class AudioCaptureService : IDisposable
    {
        /// <summary>采样率（Hz）。</summary>
        public const int SampleRate = 16000;

        /// <summary>位深（bit）。</summary>
        public const int BitsPerSample = 16;

        /// <summary>声道数。</summary>
        public const int Channels = 1;

        private readonly object _lock = new object();
        private WasapiCapture? _capture;
        private MemoryStream? _buffer;
        private bool _capturing;
        private bool _disposed;

        /// <summary>是否正在采集。</summary>
        public bool IsCapturing
        {
            get
            {
                lock (_lock)
                {
                    return _capturing;
                }
            }
        }

        /// <summary>
        /// 列出可用的麦克风设备（友好名称）。空列表表示枚举失败。
        /// </summary>
        public static IReadOnlyList<string> ListDevices()
        {
            try
            {
                var devices = new List<string>();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var endpoint in enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture, DeviceState.Active))
                {
                    devices.Add(endpoint.FriendlyName);
                }

                return devices;
            }
            catch (Exception)
            {
                // 没有音频设备 / 服务未启动：返回空列表，UI 层提示用户。
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 开始采集。传入的设备名为空时使用系统默认设备。
        /// </summary>
        /// <param name="deviceName">麦克风友好名；空 = 默认设备。</param>
        /// <param name="onSamples">实时 PCM 回调（后台线程，勿阻塞）。</param>
        public void Start(string deviceName, Action<byte[]> onSamples)
        {
            lock (_lock)
            {
                if (_disposed || _capturing)
                {
                    return;
                }

                var format = new WaveFormat(SampleRate, BitsPerSample, Channels);
                var capture = CreateCapture(deviceName, format);

                _buffer = new MemoryStream();
                _capturing = true;

                capture.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded == 0)
                    {
                        return;
                    }

                    var data = new byte[e.BytesRecorded];

                    // DataAvailableEventArgs.Buffer 已经是本次有效数据的起始位置。
                    Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);

                    lock (_lock)
                    {
                        _buffer?.Write(data, 0, data.Length);
                    }

                    try
                    {
                        onSamples(data);
                    }
                    catch (Exception)
                    {
                        // 回调异常不应中断采集。
                    }
                };

                capture.RecordingStopped += (_, _) =>
                {
                    lock (_lock)
                    {
                        _capturing = false;
                    }
                };

                capture.StartRecording();
                _capture = capture;
            }
        }

        /// <summary>
        /// 创建采集器：优先按友好名匹配设备，匹配不到就用默认设备。
        /// </summary>
        private static WasapiCapture CreateCapture(string deviceName, WaveFormat format)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    foreach (var endpoint in enumerator.EnumerateAudioEndPoints(
                        DataFlow.Capture, DeviceState.Active))
                    {
                        if (!string.Equals(endpoint.FriendlyName, deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // WASAPI 共享模式：设备流直接重采样到目标格式。
                        var capture = new WasapiCapture(endpoint, false, 100);
                        capture.WaveFormat = format;
                        return capture;
                    }
                }
                catch (Exception)
                {
                    // 匹配失败：退回默认设备。
                }
            }

            var fallback = new WasapiCapture(WasapiCapture.GetDefaultCaptureDevice(), false, 100);
            fallback.WaveFormat = format;
            return fallback;
        }

        /// <summary>停止采集。</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _capturing = false;

                if (_capture != null)
                {
                    try
                    {
                        _capture.StopRecording();
                    }
                    catch (Exception)
                    {
                        // 设备已被拔出：忽略。
                    }

                    _capture.Dispose();
                    _capture = null;
                }
            }
        }

        /// <summary>清空已录制的缓冲（唤醒后丢弃唤醒词之前的内容）。</summary>
        public void ClearBuffer()
        {
            lock (_lock)
            {
                _buffer?.SetLength(0);
            }
        }

        /// <summary>
        /// 取出已录制的音频并转成 WAV 字节流。
        /// </summary>
        public byte[] TakeWav()
        {
            lock (_lock)
            {
                if (_buffer == null)
                {
                    return Array.Empty<byte>();
                }

                byte[] pcm = _buffer.ToArray();
                _buffer.SetLength(0);

                return WavWriter.Wrap(pcm, SampleRate, BitsPerSample, Channels);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
        }
    }

    /// <summary>把裸 PCM 打包成 WAV 文件头 + 数据。</summary>
    internal static class WavWriter
    {
        public static byte[] Wrap(byte[] pcm, int sampleRate, int bitsPerSample, int channels)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;

            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + pcm.Length);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);                       // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(pcm.Length);
            writer.Write(pcm);

            writer.Flush();
            return ms.ToArray();
        }
    }
}
