using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>酷狗音乐歌词源：移动端公开接口（搜索 → 歌词候选 → 下载 LRC）。</summary>
    public sealed class KugouLyricsProvider : ILyricsProvider
    {
        private const string Referer = "http://www.kugou.com/";

        public string Name => "酷狗音乐";

        public async Task<string?> FetchLrcAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            string keyword = string.IsNullOrWhiteSpace(artist) ? title : title + " " + artist;
            string searchUrl = "http://mobilecdn.kugou.com/api/v3/search/song?format=json&page=1&pagesize=8&showtype=0&keyword="
                + Uri.EscapeDataString(keyword);

            string? searchJson = await LyricsHttp.GetAsync(searchUrl, Referer, ct);
            if (searchJson == null)
            {
                return null;
            }

            string hash = string.Empty;
            string songName = string.Empty;
            string singerName = string.Empty;
            int durationSeconds = 0;
            double bestScore = 0;

            using (JsonDocument doc = JsonDocument.Parse(searchJson))
            {
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                    || !data.TryGetProperty("info", out JsonElement info)
                    || info.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (JsonElement song in info.EnumerateArray())
                {
                    string name = song.TryGetProperty("songname", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                    string singer = song.TryGetProperty("singername", out JsonElement singerEl) ? singerEl.GetString() ?? "" : "";

                    int seconds = song.TryGetProperty("duration", out JsonElement durEl) && durEl.ValueKind == JsonValueKind.Number
                        ? durEl.GetInt32()
                        : 0;
                    TimeSpan candidateDuration = TimeSpan.FromSeconds(seconds);

                    if (!LyricsMatch.IsTrustworthy(name, singer, candidateDuration, title, artist, duration))
                    {
                        continue;
                    }

                    double score = LyricsMatch.Score(name, singer, title, artist)
                        + LyricsMatch.DurationProximity(candidateDuration, duration) * 0.5;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        hash = song.TryGetProperty("hash", out JsonElement hashEl) ? hashEl.GetString() ?? "" : "";
                        songName = name;
                        singerName = singer;
                        durationSeconds = seconds;
                    }
                }
            }

            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            // 歌词候选检索
            string lyricKeyword = Uri.EscapeDataString(singerName.Length > 0 ? songName + "-" + singerName : songName);
            string candidateUrl = "http://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword="
                + lyricKeyword + "&duration=" + durationSeconds * 1000 + "&hash=" + hash;

            string? candidateJson = await LyricsHttp.GetAsync(candidateUrl, Referer, ct);
            if (candidateJson == null)
            {
                return null;
            }

            long candidateId = 0;
            string accessKey = string.Empty;

            using (JsonDocument doc = JsonDocument.Parse(candidateJson))
            {
                if (doc.RootElement.TryGetProperty("status", out JsonElement status)
                    && status.ValueKind == JsonValueKind.Number
                    && status.GetInt32() != 200)
                {
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates)
                    || candidates.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (JsonElement candidate in candidates.EnumerateArray())
                {
                    candidateId = candidate.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt64() : 0;
                    accessKey = candidate.TryGetProperty("accesskey", out JsonElement keyEl) ? keyEl.GetString() ?? "" : "";
                    if (candidateId != 0 && accessKey.Length > 0)
                    {
                        break;
                    }
                }
            }

            if (candidateId == 0 || string.IsNullOrEmpty(accessKey))
            {
                return null;
            }

            // 下载 LRC（base64 编码的 UTF-8 文本）
            string downloadUrl = "http://lyrics.kugou.com/download?ver=1&client=pc&id=" + candidateId
                + "&accesskey=" + Uri.EscapeDataString(accessKey) + "&fmt=lrc&charset=utf8";

            string? downloadJson = await LyricsHttp.GetAsync(downloadUrl, Referer, ct);
            if (downloadJson == null)
            {
                return null;
            }

            using JsonDocument downloadDoc = JsonDocument.Parse(downloadJson);
            if (!downloadDoc.RootElement.TryGetProperty("content", out JsonElement content))
            {
                return null;
            }

            string? base64 = content.GetString();
            if (string.IsNullOrWhiteSpace(base64))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
