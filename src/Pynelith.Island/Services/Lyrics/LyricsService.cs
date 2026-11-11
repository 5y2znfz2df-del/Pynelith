using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pynelith.Island.Configuration;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>
    /// 歌词服务：按「歌手 - 标题」归一化键组织本地 LRC 缓存（%AppData%\Pynelith\lyrics），
    /// 命中缓存直接返回；未命中时按配置启用的歌词源（网易云 → QQ音乐 → 酷狗）依次抓取，
    /// 成功后写入缓存。手动导入的 LRC 也落到同一缓存目录，立即对当前曲目生效。
    /// </summary>
    public sealed class LyricsService
    {
        private readonly IslandConfig _config;
        private readonly Dictionary<string, LrcDocument> _memory = new Dictionary<string, LrcDocument>();
        private readonly NeteaseLyricsProvider _netease = new NeteaseLyricsProvider();
        private readonly QQMusicLyricsProvider _qq = new QQMusicLyricsProvider();
        private readonly KugouLyricsProvider _kugou = new KugouLyricsProvider();

        /// <summary>手动导入完成后触发（岛侧据此对当前曲目立即重新匹配）。</summary>
        public event EventHandler? ManualImported;

        public LyricsService(IslandConfig config)
        {
            _config = config;

            try
            {
                Directory.CreateDirectory(LyricsDirectory);
            }
            catch (Exception)
            {
                // 目录不可用时退化为纯内存缓存。
            }
        }

        /// <summary>歌词缓存目录（含手动导入）。</summary>
        public static string LyricsDirectory => Path.Combine(ConfigStore.Directory, "lyrics");

        /// <summary>
        /// 获取曲目歌词。找不到时返回空文档（缓存负结果，避免重复请求）。
        /// 曲目切换时由调用方取消上一次查询。
        /// </summary>
        public async Task<LrcDocument> GetLyricsAsync(
            string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return LrcDocument.Empty;
            }

            string key = MakeKey(title, artist);

            lock (_memory)
            {
                if (_memory.TryGetValue(key, out LrcDocument? cached))
                {
                    return cached;
                }
            }

            LrcDocument fromDisk = TryLoadFromDisk(key);
            if (!fromDisk.IsEmpty && FitsDuration(fromDisk, duration))
            {
                Remember(key, fromDisk);
                return fromDisk;
            }

            // 注意：这里不再做"按标题模糊命中缓存"的回退。
            // 之前只要缓存文件名包含标题就返回，经常把同名不同版本 / 同名不同歌手的歌词
            // 套到当前曲目上，表现为"突然冒出来的 LRC 对不上"。

            foreach (ILyricsProvider provider in EnabledProviders())
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string? lrc = await provider.FetchLrcAsync(title, artist, duration, ct);
                    LrcDocument doc = LrcDocument.Parse(lrc);
                    if (doc.IsEmpty || !FitsDuration(doc, duration))
                    {
                        // 时间轴比歌曲还长出一大截：多半是抓到了另一版本/另一首歌，丢弃
                        continue;
                    }

                    SaveToDisk(key, lrc!);
                    Remember(key, doc);
                    return doc;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 单一歌词源失败不影响后续源。
                }
            }

            Remember(key, LrcDocument.Empty);
            return LrcDocument.Empty;
        }

        /// <summary>手动导入 LRC 文件并关联到指定曲目（覆盖已有缓存）。</summary>
        public void ImportLrc(string sourceFile, string title, string artist)
        {
            string text = File.ReadAllText(sourceFile);
            LrcDocument doc = LrcDocument.Parse(text);
            if (doc.IsEmpty)
            {
                throw new InvalidDataException("文件中没有可解析的 LRC 时间轴");
            }

            string key = MakeKey(title, artist);
            SaveToDisk(key, text);
            Remember(key, doc);
            ManualImported?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>生成「歌手 - 标题」归一化键（同时用作缓存文件名）。</summary>
        public static string MakeKey(string title, string artist)
        {
            string raw = string.IsNullOrWhiteSpace(artist)
                ? title.Trim()
                : artist.Trim() + " - " + title.Trim();

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                raw = raw.Replace(invalid, '_');
            }

            raw = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            return raw.Length > 96 ? raw.Substring(0, 96) : raw;
        }

        private IEnumerable<ILyricsProvider> EnabledProviders()
        {
            if (_config.LyricsSourceNetease)
            {
                yield return _netease;
            }

            if (_config.LyricsSourceQQ)
            {
                yield return _qq;
            }

            if (_config.LyricsSourceKugou)
            {
                yield return _kugou;
            }
        }

        private static LrcDocument TryLoadFromDisk(string key)
        {
            try
            {
                string path = Path.Combine(LyricsDirectory, key + ".lrc");
                if (!File.Exists(path))
                {
                    return LrcDocument.Empty;
                }

                return LrcDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return LrcDocument.Empty;
            }
        }

        /// <summary>
        /// 歌词最后一行时间不应明显超过歌曲时长（允许 20 秒片尾余量）。
        /// 用来识别"同名但不同版本/不同歌"的错误缓存，避免整首歌词错位。
        /// </summary>
        private static bool FitsDuration(LrcDocument doc, TimeSpan duration)
        {
            if (doc.IsEmpty || duration <= TimeSpan.Zero)
            {
                return true;
            }

            TimeSpan last = doc.Lines[doc.Lines.Count - 1].Time;
            return last <= duration + TimeSpan.FromSeconds(20);
        }

        private static void SaveToDisk(string key, string lrcText)
        {
            try
            {
                Directory.CreateDirectory(LyricsDirectory);
                File.WriteAllText(Path.Combine(LyricsDirectory, key + ".lrc"), lrcText);
            }
            catch (Exception)
            {
                // 写盘失败时仅用内存缓存。
            }
        }

        private void Remember(string key, LrcDocument doc)
        {
            lock (_memory)
            {
                _memory[key] = doc;
            }
        }
    }
}
