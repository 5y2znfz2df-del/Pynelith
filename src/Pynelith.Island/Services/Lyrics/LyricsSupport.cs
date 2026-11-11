using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services.Lyrics
{
    /// <summary>歌词源：按曲目信息抓取原始 LRC 文本，失败返回 null。</summary>
    public interface ILyricsProvider
    {
        string Name { get; }

        Task<string?> FetchLrcAsync(string title, string artist, TimeSpan duration, CancellationToken ct);
    }

    /// <summary>歌词源共用的 HTTP 通道（统一 UA、超时、压缩）。</summary>
    internal static class LyricsHttp
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate,
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");

            return client;
        }

        public static async Task<string?> GetAsync(string url, string referer, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(referer);

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }

    /// <summary>候选曲目与目标曲目的模糊匹配（标题互相包含为必要条件，歌手相近加分）。</summary>
    internal static class LyricsMatch
    {
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char ch in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        /// <summary>歌手是否可视为同一人（互相包含；目标没歌手时视为兼容）。</summary>
        public static bool ArtistCompatible(string candArtist, string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
            {
                return true;
            }

            string target = Normalize(artist);
            string cand = Normalize(candArtist);
            if (target.Length == 0 || cand.Length == 0)
            {
                return false;
            }

            return cand.Contains(target) || target.Contains(cand);
        }

        /// <summary>
        /// 候选曲目打分：标题完全相等 3 分、仅包含 1.5 分，歌手兼容再 +2。
        /// 只用于"择优"，是否可信由 <see cref="IsTrustworthy"/> 决定。
        /// </summary>
        public static double Score(string candTitle, string candArtist, string title, string artist)
        {
            string target = Normalize(title);
            string cand = Normalize(candTitle);
            if (target.Length == 0 || cand.Length == 0)
            {
                return 0;
            }

            if (!cand.Contains(target) && !target.Contains(cand))
            {
                return 0;
            }

            double score = target == cand ? 3 : 1.5;
            if (ArtistCompatible(candArtist, artist))
            {
                score += 2;
            }

            return score;
        }

        /// <summary>
        /// 硬性可信校验：标题必须匹配；标题只是"包含"时歌手也必须匹配；
        /// 双方时长相差超过 12 秒视为不同版本（Live / Remix / 翻唱），直接拒绝。
        /// </summary>
        public static bool IsTrustworthy(
            string candTitle,
            string candArtist,
            TimeSpan candDuration,
            string title,
            string artist,
            TimeSpan duration)
        {
            string target = Normalize(title);
            string cand = Normalize(candTitle);
            if (target.Length == 0 || cand.Length == 0)
            {
                return false;
            }

            bool exact = string.Equals(cand, target, StringComparison.Ordinal);
            if (!exact
                && !cand.Contains(target)
                && !target.Contains(cand))
            {
                return false;
            }

            if (!exact && !ArtistCompatible(candArtist, artist))
            {
                return false;
            }

            if (duration > TimeSpan.Zero
                && candDuration > TimeSpan.Zero
                && Math.Abs((candDuration - duration).TotalSeconds) > 12)
            {
                return false;
            }

            return true;
        }

        /// <summary>时长的接近程度（用于同分候选的择优），返回 0..1。</summary>
        public static double DurationProximity(TimeSpan candidate, TimeSpan target)
        {
            if (target <= TimeSpan.Zero || candidate <= TimeSpan.Zero)
            {
                return 0.5;
            }

            double diff = Math.Abs((candidate - target).TotalSeconds);
            return Math.Clamp(1 - diff / 12.0, 0, 1);
        }
    }
}
