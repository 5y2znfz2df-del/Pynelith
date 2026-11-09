using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>网易云音乐歌词源：网页端公开接口（搜索 → 按曲目匹配 → 取 LRC）。</summary>
    public sealed class NeteaseLyricsProvider : ILyricsProvider
    {
        private const string Referer = "https://music.163.com/";

        public string Name => "网易云音乐";

        public async Task<string?> FetchLrcAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            string keyword = string.IsNullOrWhiteSpace(artist) ? title : title + " " + artist;
            string searchUrl = "https://music.163.com/api/search/get/web?csrf_token=hlpretag=&hlposttag=&s="
                + Uri.EscapeDataString(keyword) + "&type=1&offset=0&total=true&limit=8";

            string? searchJson = await LyricsHttp.GetAsync(searchUrl, Referer, ct);
            if (searchJson == null)
            {
                return null;
            }

            long songId = 0;
            double bestScore = 0;
            double bestProximity = 0;

            using (JsonDocument doc = JsonDocument.Parse(searchJson))
            {
                if (!doc.RootElement.TryGetProperty("result", out JsonElement result)
                    || !result.TryGetProperty("songs", out JsonElement songs))
                {
                    return null;
                }

                foreach (JsonElement song in songs.EnumerateArray())
                {
                    string name = song.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                    string singer = ArtistText(song);
                    double score = LyricsMatch.Score(name, singer, title, artist);
                    if (score < 2)
                    {
                        continue;
                    }

                    long candidateMs = song.TryGetProperty("duration", out JsonElement durEl) && durEl.ValueKind == JsonValueKind.Number
                        ? durEl.GetInt64()
                        : 0;
                    double proximity = LyricsMatch.DurationProximity(TimeSpan.FromMilliseconds(candidateMs), duration);

                    if (score > bestScore || (Math.Abs(score - bestScore) < 0.01 && proximity > bestProximity))
                    {
                        bestScore = score;
                        bestProximity = proximity;
                        songId = song.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt64() : 0;
                    }
                }
            }

            if (songId == 0)
            {
                return null;
            }

            string lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1";
            string? lyricJson = await LyricsHttp.GetAsync(lyricUrl, Referer, ct);
            if (lyricJson == null)
            {
                return null;
            }

            using JsonDocument lyricDoc = JsonDocument.Parse(lyricJson);
            if (lyricDoc.RootElement.TryGetProperty("lrc", out JsonElement lrc)
                && lrc.TryGetProperty("lyric", out JsonElement lyric))
            {
                return lyric.GetString();
            }

            return null;
        }

        private static string ArtistText(JsonElement song)
        {
            if (!song.TryGetProperty("artists", out JsonElement artists)
                || artists.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var names = new System.Text.StringBuilder();
            foreach (JsonElement artist in artists.EnumerateArray())
            {
                if (artist.TryGetProperty("name", out JsonElement name))
                {
                    if (names.Length > 0)
                    {
                        names.Append('/');
                    }

                    names.Append(name.GetString());
                }
            }

            return names.ToString();
        }
    }
}
