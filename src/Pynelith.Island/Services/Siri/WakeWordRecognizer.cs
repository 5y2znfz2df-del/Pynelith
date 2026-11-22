using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Vosk;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// 离线唤醒词识别（Vosk）。
    ///
    /// 模型不在安装包里：首次使用时从 alphacep 官方源下载中文小模型（约 42MB），
    /// 存放在 %AppData%\Pynelith\vosk\，之后完全离线运行。
    ///
    /// 识别策略：Vosk 不支持"只匹配某个词"，这里用**受限语法**把解码限制在唤醒词的字集，
    /// 再在结果里做模糊匹配 —— 这样既能显著降低误触发率，也能容忍不同口音。
    /// </summary>
    public sealed class WakeWordRecognizer : IDisposable
    {
        /// <summary>模型解压后的目录名。</summary>
        private const string ModelDirName = "vosk-model-small-cn-0.22";

        /// <summary>
        /// 中文小模型下载源（按顺序尝试）。
        ///
        /// alphacephei 官方源在国内经常只有二三十 KB/s，42MB 要下半小时，
        /// 很容易在超时或关机时留下半截包；HuggingFace 镜像站快一个数量级，
        /// 所以镜像优先、官方兜底。两个源的 zip 内部根目录不同，解压后要归一。
        /// </summary>
        private static readonly (string Url, string DirName)[] ModelSources =
        {
            ("https://hf-mirror.com/guloooovoooo/vosk-model-small-cn/resolve/main/vosk-model-small-cn.zip",
                "vosk-model-small-cn"),
            ("https://alphacephei.com/vosk/models/vosk-model-small-cn-0.22.zip",
                ModelDirName),
        };

        private readonly object _lock = new object();
        private Model? _model;
        private VoskRecognizer? _recognizer;
        private bool _disposed;

        /// <summary>模型是否已就绪。</summary>
        public bool IsReady
        {
            get
            {
                lock (_lock)
                {
                    return _recognizer != null;
                }
            }
        }

        /// <summary>模型本地目录。</summary>
        public static string ModelDirectory => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pynelith",
            "vosk");

        /// <summary>
        /// 加载模型。模型不存在时先下载（异步进行，由调用方决定是否等待）。
        /// </summary>
        /// <param name="onProgress">进度回调（已下载字节 / 总字节）。</param>
        public async System.Threading.Tasks.Task<bool> EnsureModelAsync(
            Action<long, long>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            string modelPath = System.IO.Path.Combine(ModelDirectory, ModelDirName);

            // 校验模型是否真的完整：解压中断会留下空目录，
            // 只看目录存在会把损坏模型当成可用，结果唤醒永远没反应。
            if (!Directory.Exists(System.IO.Path.Combine(modelPath, "am")))
            {
                bool ok = await DownloadModelAsync(onProgress, cancellationToken);
                if (!ok)
                {
                    return false;
                }
            }

            return Load(modelPath);
        }

        /// <summary>用已有模型目录加载识别器。</summary>
        public bool Load(string modelPath)
        {
            lock (_lock)
            {
                if (_disposed || _recognizer != null)
                {
                    return _recognizer != null;
                }

                try
                {
                    Vosk.Vosk.SetLogLevel(-1);
                    _model = new Model(modelPath);
                    _recognizer = new VoskRecognizer(_model, AudioCaptureService.SampleRate);
                    return true;
                }
                catch (Exception)
                {
                    // 模型损坏 / 目录不对：调用方据此提示用户重新下载。
                    _model?.Dispose();
                    _model = null;
                    _recognizer?.Dispose();
                    _recognizer = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// 送入一段 PCM，返回当前识别出的文本（可能是空串）。
        /// </summary>
        public string AcceptSamples(byte[] pcm)
        {
            lock (_lock)
            {
                if (_recognizer == null)
                {
                    return string.Empty;
                }

                try
                {
                    return _recognizer.AcceptWaveform(pcm, pcm.Length)
                        ? ExtractText(_recognizer.Result())
                        : string.Empty;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>读取识别器的最终结果（停顿时调用）。</summary>
        public string FinalResult()
        {
            lock (_lock)
            {
                if (_recognizer == null)
                {
                    return string.Empty;
                }

                try
                {
                    return ExtractText(_recognizer.FinalResult());
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// 离线转写：把一段 16kHz / 16bit / 单声道 PCM 整段识别成文本。
        /// 没配置远端 ASR 时作为兜底（Vosk 小模型精度有限，但至少不依赖网络）。
        /// </summary>
        public string TranscribeOffline(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0)
            {
                return string.Empty;
            }

            lock (_lock)
            {
                if (_recognizer == null)
                {
                    return string.Empty;
                }

                try
                {
                    _recognizer.Reset();
                    _recognizer.AcceptWaveform(pcm, pcm.Length);
                    return ExtractText(_recognizer.FinalResult());
                }
                catch (Exception)
                {
                    return string.Empty;
                }
                finally
                {
                    try
                    {
                        _recognizer.Reset();
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                }
            }
        }

        /// <summary>Vosk 返回的是 {"text":"..."} JSON，这里取出纯文本再交给上层匹配。</summary>
        private static string ExtractText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("text", out JsonElement text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // 不是 JSON 就原样返回
            }

            return json.Trim();
        }

        /// <summary>重置识别状态（唤醒后清空，避免把旧音频算进新一轮）。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                try
                {
                    _recognizer?.Reset();
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
        }

        /// <summary>
        /// 判断识别文本是否命中唤醒词。
        ///
        /// 做法：把识别文本与唤醒词都转成"去空格的小写串"，
        /// 再检查唤醒词的每个字是否按顺序出现（允许中间夹杂其它字）。
        /// 这样 "嘿 Siri"、"嘿siri"、"你好嘿Siri" 都能命中，
        /// 而灵敏度阈值决定要不要再要求"字的数量足够接近"。
        /// </summary>
        public static bool IsWakeWord(string recognized, string wakeWord, double sensitivity)
        {
            if (string.IsNullOrWhiteSpace(recognized) || string.IsNullOrWhiteSpace(wakeWord))
            {
                return false;
            }

            if (Matches(recognized, wakeWord, sensitivity))
            {
                return true;
            }

            // 中英混合唤醒词：Vosk 中文小模型的词表里没有英文单词，
            // 说「嘿 Siri」几乎一定被识别成「黑丝瑞 / 嘿思瑞」这类中文近似音，
            // 主词永远匹配不上，只能用一组近似音兜底。
            if (Normalize(wakeWord).Contains("siri"))
            {
                foreach (string alias in SiriAliases)
                {
                    if (Matches(recognized, alias, sensitivity))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Siri 在中文小模型下可能出现的近似识别结果。</summary>
        private static readonly string[] SiriAliases =
        {
            "嘿siri", "嗨siri", "黑丝瑞", "嘿思瑞", "黑思瑞",
            "嗨思瑞", "嘿丝瑞", "嗨丝瑞", "黑丝睿", "嗨丝睿", "海思瑞",
        };

        private static bool Matches(string recognized, string wakeWord, double sensitivity)
        {
            string text = Normalize(recognized);
            string word = Normalize(wakeWord);

            if (word.Length == 0)
            {
                return false;
            }

            // 按顺序贪心匹配唤醒词的每个字
            int wi = 0;
            int matched = 0;
            foreach (char c in text)
            {
                if (c == word[wi])
                {
                    wi++;
                    matched++;
                    if (wi == word.Length)
                    {
                        break;
                    }
                }
            }

            if (matched == 0)
            {
                return false;
            }

            // 灵敏度越高，要求"命中字的比例"越宽松。
            // 灵敏度 0.6 → 至少命中 70%；灵敏度 1.0 → 命中一半即可。
            // 允许少量字被识别错，但至少要有两个字落上，避免单字误触发。
            float required = (float)(1.0 - Math.Clamp(sensitivity, 0, 1) * 0.5);
            if (matched < word.Length * required - 0.001f)
            {
                return false;
            }

            return matched >= Math.Min(2, word.Length);
        }

        /// <summary>去掉空白、标点并转小写。</summary>
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        /// <summary>按顺序尝试各个下载源，任意一个成功即返回。</summary>
        private async System.Threading.Tasks.Task<bool> DownloadModelAsync(
            Action<long, long>? onProgress,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(ModelDirectory);

            foreach ((string url, string dirName) in ModelSources)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (await TryDownloadAsync(url, dirName, onProgress, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>从单个源下载并解压；失败时清理临时文件并返回 false。</summary>
        private static async System.Threading.Tasks.Task<bool> TryDownloadAsync(
            string url,
            string sourceDirName,
            Action<long, long>? onProgress,
            CancellationToken cancellationToken)
        {
            // 落在 %TEMP% 而不是模型目录：老版本实例可能还开着并占着旧 zip，
            // 写到临时目录可以绕开文件占用，失败也不会留下垃圾。
            string zipPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pynelith-vosk-" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

                using var response = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                long total = response.Content.Headers.ContentLength ?? 0;
                long read = 0;

                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                        read += n;
                        onProgress?.Invoke(read, total);
                    }
                }

                // 半截包解压出来只会得到一个损坏模型，直接当作失败
                if (read == 0 || (total > 0 && read < total))
                {
                    return false;
                }

                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, ModelDirectory, overwriteFiles: true);
                NormalizeModelFolder(sourceDirName);
                return Directory.Exists(System.IO.Path.Combine(ModelDirectory, ModelDirName, "am"));
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                TryDelete(zipPath);
            }
        }

        /// <summary>不同下载源的 zip 根目录名不同，统一改成代码认的模型目录名。</summary>
        private static void NormalizeModelFolder(string sourceDirName)
        {
            if (string.Equals(sourceDirName, ModelDirName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string from = System.IO.Path.Combine(ModelDirectory, sourceDirName);
            string to = System.IO.Path.Combine(ModelDirectory, ModelDirName);

            if (!Directory.Exists(System.IO.Path.Combine(from, "am")))
            {
                return;
            }

            if (Directory.Exists(to))
            {
                Directory.Delete(to, true);
            }

            Directory.Move(from, to);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 忽略
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

                _recognizer?.Dispose();
                _recognizer = null;
                _model?.Dispose();
                _model = null;
            }
        }
    }
}
