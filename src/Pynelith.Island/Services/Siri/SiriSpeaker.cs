using System;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// 回复播报（TTS）。
    ///
    /// 默认走 Microsoft Edge 的在线神经网络语音（zh-CN-XiaoxiaoNeural 等），
    /// 这是 Windows 上最接近 Siri 的自然中文音色，无需任何密钥与账号；
    /// 在线不可用时自动退回本机 SAPI 语音，保证"总能出声"。
    /// 合成与播放全程在后台线程，播放器可随时打断。
    /// </summary>
    public sealed class SiriSpeaker : IDisposable
    {
        /// <summary>Edge 朗读接口的公开客户端令牌（浏览器"大声朗读"用的同一套）。</summary>
        internal const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

        /// <summary>令牌版本串，需与 Sec-MS-GEC 一起发送，否则服务端 403。</summary>
        private const string EdgeVersion = "2-143.0.3650.75";

        private const string EdgeOrigin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

        /// <summary>系统离线语音的标识值（下拉选项里选"系统语音"时存这个）。</summary>
        public const string SystemVoice = "system";

        private readonly object _gate = new object();
        private CancellationTokenSource? _cts;
        private WaveOutEvent? _output;
        private WaveStream? _reader;
        private bool _disposed;

        /// <summary>是否正在播报。</summary>
        public bool IsSpeaking { get; private set; }

        /// <summary>最近一次播报的错误（成功时为空）。</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// 播报一段文字。会先打断上一段，立即返回。
        /// </summary>
        /// <param name="text">待播报文本（会自动去掉 Markdown 标记）。</param>
        /// <param name="voice">Edge 音色名；<see cref="SystemVoice"/> 表示本机语音。</param>
        /// <param name="ratePercent">语速偏移百分比，-50 ~ 50。</param>
        public void Speak(string text, string voice, int ratePercent)
        {
            string spoken = Clean(text);
            if (_disposed || spoken.Length == 0)
            {
                return;
            }

            Stop();

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _cts = cts;
            }

            IsSpeaking = true;
            LastError = string.Empty;

            _ = Task.Run(() => RunAsync(spoken, voice, ratePercent, cts));
        }

        /// <summary>立即停止播报（不抛异常）。</summary>
        public void Stop()
        {
            CancellationTokenSource? cts;
            lock (_gate)
            {
                cts = _cts;
                _cts = null;
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经释放，忽略。
            }

            WaveOutEvent? output;
            WaveStream? reader;
            lock (_gate)
            {
                output = _output;
                reader = _reader;
                _output = null;
                _reader = null;
            }

            try
            {
                output?.Stop();
            }
            catch (Exception)
            {
                // 设备已关闭时 Stop 会抛，忽略即可。
            }

            output?.Dispose();
            reader?.Dispose();

            IsSpeaking = false;
        }

        private async Task RunAsync(string text, string voice, int ratePercent, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            byte[] audio;

            try
            {
                if (string.Equals(voice, SystemVoice, StringComparison.OrdinalIgnoreCase))
                {
                    audio = await Task.Run(() => SynthesizeSystem(text, ratePercent), token);
                }
                else
                {
                    audio = await SynthesizeEdgeAsync(text, voice, ratePercent, token);
                }
            }
            catch (OperationCanceledException)
            {
                Finish(cts);
                return;
            }
            catch (Exception ex)
            {
                // 在线语音失败（断网 / 接口变更）时退回本机语音，避免"没反应"。
                try
                {
                    audio = await Task.Run(() => SynthesizeSystem(text, ratePercent), token);
                    LastError = "在线语音不可用，已改用系统语音：" + ex.Message;
                }
                catch (Exception fallbackEx)
                {
                    LastError = "语音播报失败：" + fallbackEx.Message;
                    Finish(cts);
                    return;
                }
            }

            if (token.IsCancellationRequested)
            {
                Finish(cts);
                return;
            }

            try
            {
                Play(audio, token);
            }
            catch (Exception ex)
            {
                LastError = "播放失败：" + ex.Message;
                Finish(cts);
            }
        }

        /// <summary>播放合成好的音频（WAV 或 MP3 自适应）。</summary>
        private void Play(byte[] audio, CancellationToken token = default)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var stream = new MemoryStream(audio, writable: false);
            WaveStream reader = LooksLikeWav(audio)
                ? new WaveFileReader(stream)
                : new Mp3FileReader(stream);

            var output = new WaveOutEvent { DesiredLatency = 140, Volume = 0.95f };
            output.Init(reader);

            lock (_gate)
            {
                _output = output;
                _reader = reader;
            }

            output.PlaybackStopped += (_, _) =>
            {
                WaveOutEvent? stopped;
                WaveStream? source;
                lock (_gate)
                {
                    stopped = ReferenceEquals(_output, output) ? _output : null;
                    source = stopped != null ? _reader : null;
                    if (stopped != null)
                    {
                        _output = null;
                        _reader = null;
                    }
                }

                stopped?.Dispose();
                source?.Dispose();

                // 只有"当前这一段"结束才复位状态：被打断的旧段不能清掉新段的播报标记
                if (stopped != null)
                {
                    IsSpeaking = false;
                }
            };

            output.Play();
        }

        private void Finish(CancellationTokenSource cts)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                }
            }

            IsSpeaking = false;
            cts.Dispose();
        }

        #region 在线音频（Edge 神经网络语音）

        /// <summary>连接 Edge 朗读接口，合成 MP3 音频。</summary>
        private static async Task<byte[]> SynthesizeEdgeAsync(
            string text,
            string voice,
            int ratePercent,
            CancellationToken token)
        {
            string url =
                "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1"
                + "?TrustedClientToken=" + TrustedClientToken
                + "&ConnectionId=" + Guid.NewGuid().ToString("N")
                + "&Sec-MS-GEC=" + Uri.EscapeDataString(EdgeTtsToken.Generate())
                + "&Sec-MS-GEC-Version=" + EdgeVersion;

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", EdgeOrigin);
            socket.Options.SetRequestHeader("Pragma", "no-cache");
            socket.Options.SetRequestHeader("Cache-Control", "no-cache");
            socket.Options.SetRequestHeader("User-Agent", UserAgent);
            socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
            socket.Options.SetRequestHeader("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");

            await socket.ConnectAsync(new Uri(url), token);

            await SendAsync(socket, BuildConfigMessage(), token);
            await SendAsync(socket, BuildSsmlMessage(text, voice, ratePercent), token);

            using var audio = new MemoryStream();
            var buffer = new byte[16384];
            bool finished = false;

            while (!finished && socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        finished = true;
                        break;
                    }

                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (finished)
                {
                    break;
                }

                byte[] payload = frame.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(payload);
                    if (message.Contains("Path:turn.end", StringComparison.Ordinal))
                    {
                        break;
                    }

                    continue;
                }

                // 二进制帧：前两字节是大端序的头部长度，头部里写明 Path:audio。
                if (payload.Length < 2)
                {
                    continue;
                }

                int headerLength = (payload[0] << 8) | payload[1];
                int dataStart = 2 + headerLength;
                if (payload.Length <= dataStart)
                {
                    continue;
                }

                string header = Encoding.UTF8.GetString(payload, 2, headerLength);
                if (header.Contains("Path:audio", StringComparison.Ordinal))
                {
                    audio.Write(payload, dataStart, payload.Length - dataStart);
                }
            }

            byte[] bytes = audio.ToArray();
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("在线语音没有返回音频。");
            }

            return bytes;
        }

        private static Task SendAsync(ClientWebSocket socket, string message, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            return socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private static string BuildConfigMessage()
        {
            const string Options =
                "{\"context\":{\"synthesis\":{\"audio\":{"
                + "\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},"
                + "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

            return "X-Timestamp:" + Timestamp() + "\r\n"
                + "Content-Type:application/json; charset=utf-8\r\n"
                + "Path:speech.config\r\n\r\n"
                + Options;
        }

        private static string BuildSsmlMessage(string text, string voice, int ratePercent)
        {
            int rate = Math.Clamp(ratePercent, -50, 50);
            string rateText = (rate >= 0 ? "+" : string.Empty)
                + rate.ToString(CultureInfo.InvariantCulture) + "%";

            string ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>"
                + "<voice name='" + Escape(voice) + "'>"
                + "<prosody rate='" + rateText + "' pitch='+0Hz'>" + Escape(text) + "</prosody>"
                + "</voice></speak>";

            return "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\n"
                + "Content-Type:application/ssml+xml\r\n"
                + "X-Timestamp:" + Timestamp() + "\r\n"
                + "Path:ssml\r\n\r\n"
                + ssml;
        }

        private static string Timestamp()
            => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        #endregion

        #region 本机语音（离线兜底）

        /// <summary>用系统 SAPI 语音合成 WAV。</summary>
        private static byte[] SynthesizeSystem(string text, int ratePercent)
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();

            try
            {
                synth.SelectVoiceByHints(
                    System.Speech.Synthesis.VoiceGender.Female,
                    System.Speech.Synthesis.VoiceAge.Adult,
                    0,
                    new CultureInfo("zh-CN"));
            }
            catch (Exception)
            {
                // 没有中文女声时用系统默认语音。
            }

            int rate = Math.Clamp(ratePercent, -50, 50);
            synth.Rate = (int)Math.Round(rate / 10.0);

            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);
            return ms.ToArray();
        }

        #endregion

        /// <summary>去掉 Markdown / 表情 / 代码块，只留可朗读的正文。</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            bool inCodeBlock = false;

            foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }

                if (inCodeBlock)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(line);
            }

            string result = builder.ToString();

            // 链接与图片只留文字：![alt](url) / [text](url)
            result = System.Text.RegularExpressions.Regex.Replace(result, @"!\[[^\]]*\]\([^)]*\)", string.Empty);
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\[([^\]]*)\]\([^)]*\)", "$1");

            // 强调符号、标题井号、行内代码、引用符
            result = result.Replace("**", string.Empty)
                .Replace("__", string.Empty)
                .Replace("`", string.Empty)
                .Replace("###", string.Empty)
                .Replace("##", string.Empty)
                .Replace(">", string.Empty)
                .Replace("*", string.Empty)
                .Replace("#", string.Empty);

            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

            // 表情与符号：只保留中英文字母、数字与常用标点
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"[^\p{L}\p{N}\p{P}\p{Z}\n]",
                string.Empty);

            // 太长会念到让人失去耐心，只播前面一段
            const int MaxLength = 600;
            if (result.Length > MaxLength)
            {
                result = result.Substring(0, MaxLength).TrimEnd() + "。";
            }

            return result.Trim();
        }

        private static bool LooksLikeWav(byte[] data)
            => data.Length > 12
               && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F';

        private static string Escape(string value)
            => value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("'", "&apos;")
                .Replace("\"", "&quot;");

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }
    }
}
