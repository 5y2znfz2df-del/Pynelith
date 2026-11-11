using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>QQ音乐歌词源：网页端公开接口（搜索 → 按曲目匹配 → 取 LRC）。</summary>
    public sealed class QQMusicLyricsProvider : ILyricsProvider
    {
        private const string Referer = "https://y.qq.com/";
        private const string LyricReferer = "https://y.qq.com/portal/player.html";

        public string Name => "QQ音乐";

        public async Task<string?> FetchLrcAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            string keyword = string.IsNullOrWhiteSpace(artist) ? title : title + " " + artist;
            string searchUrl = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?format=json&platform=yqq"
                + "&needNewCode=1&key=" + Uri.EscapeDataString(keyword);

            string? searchJson = await LyricsHttp.GetAsync(searchUrl, Referer, ct);
            if (searchJson == null)
            {
                return null;
            }

            string songMid = string.Empty;
            double bestScore = 0;

            using (JsonDocument doc = JsonDocument.Parse(searchJson))
            {
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                    || !data.TryGetProperty("song", out JsonElement songRoot)
                    || !songRoot.TryGetProperty("itemlist", out JsonElement list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (JsonElement song in list.EnumerateArray())
                {
                    string name = song.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                    string singer = song.TryGetProperty("singer", out JsonElement singerEl) ? singerEl.GetString() ?? "" : "";

                    // 搜索结果不含时长，按标题 + 歌手做硬性校验
                    if (!LyricsMatch.IsTrustworthy(name, singer, TimeSpan.Zero, title, artist, duration))
                    {
                        continue;
                    }

                    double score = LyricsMatch.Score(name, singer, title, artist);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        songMid = song.TryGetProperty("mid", out JsonElement midEl) ? midEl.GetString() ?? "" : "";
                    }
                }
            }

            if (string.IsNullOrEmpty(songMid))
            {
                return null;
            }

            string lyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
                + "?songmid=" + Uri.EscapeDataString(songMid)
                + "&format=json&nobase64=1&inCharset=utf8&outCharset=utf-8";

            string? lyricJson = await LyricsHttp.GetAsync(lyricUrl, LyricReferer, ct);
            if (lyricJson == null)
            {
                return null;
            }

            using JsonDocument lyricDoc = JsonDocument.Parse(lyricJson);
            if (lyricDoc.RootElement.TryGetProperty("lyric", out JsonElement lyric))
            {
                return lyric.GetString();
            }

            return null;
        }
    }
}
