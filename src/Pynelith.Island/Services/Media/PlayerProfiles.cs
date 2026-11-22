using System;
using System.Collections.Generic;

namespace Pynelith.Island.Services.Media
{
    /// <summary>
    /// 已知播放器的画像。
    ///
    /// 存在的理由：不同播放器向 SMTC 汇报信息的能力差别很大。
    /// 网易云音乐的 SMTC 时间轴 <b>Position 恒为 0</b>，只有总时长可用；
    /// QQ 音乐 / Spotify 等会正常上报进度。同一个"曲目元数据变化"事件，
    /// 在上报进度的播放器上可以直接采信，在不上报的播放器上只能当作"新歌 0 点"的近似。
    ///
    /// 这里集中登记每个播放器的：
    /// - 识别特征（AUMID 关键字 / 进程名）；
    /// - 是否上报进度（决定时间轴能不能采信）；
    /// - 窗口标题里歌名的分隔符（用于辅助同步时拆出歌名与歌手）。
    /// </summary>
    public sealed class PlayerProfile
    {
        /// <summary>稳定的播放器标识（用于配置与预设库的主键）。</summary>
        public string Key { get; init; } = "unknown";

        /// <summary>界面显示名。</summary>
        public string DisplayName { get; init; } = "未知播放器";

        /// <summary>AUMID 里包含这些关键字之一即视为该播放器。</summary>
        public string[] AumidKeywords { get; init; } = Array.Empty<string>();

        /// <summary>进程名（不含扩展名）匹配这些关键字即视为该播放器。</summary>
        public string[] ProcessKeywords { get; init; } = Array.Empty<string>();

        /// <summary>该播放器是否会上报真实播放进度。</summary>
        public bool ReportsProgress { get; init; } = true;

        /// <summary>
        /// 该播放器上报进度时，元数据事件与音频起点之间的固定延迟（毫秒）。
        /// 为正表示"元数据来得比音频晚"，用这个值把 0 点提前。
        /// </summary>
        public int MetadataLagMs { get; init; }

        /// <summary>窗口标题中歌名与歌手之间的分隔符。</summary>
        public string TitleSeparator { get; init; } = " - ";
    }

    /// <summary>
    /// 内置播放器画像库。新增播放器只需在这里登记一条。
    /// </summary>
    public static class PlayerProfiles
    {
        /// <summary>网易云音乐：不上报进度，靠曲目切换事件推算 0 点。</summary>
        public static readonly PlayerProfile Netease = new()
        {
            Key = "netease",
            DisplayName = "网易云音乐",
            AumidKeywords = new[] { "netease", "cloudmusic", "163music", "orpheus" },
            ProcessKeywords = new[] { "cloudmusic", "netease" },
            ReportsProgress = false,
            // 网易云的 MediaPropertiesChanged 通常在音频真正开始后 200–600ms 才到，
            // 元数据越晚，"换歌 0 点"就越晚，歌词就越晚出现。
            MetadataLagMs = 350,
            TitleSeparator = " - ",
        };

        /// <summary>QQ 音乐：正常上报进度。</summary>
        public static readonly PlayerProfile QQMusic = new()
        {
            Key = "qqmusic",
            DisplayName = "QQ音乐",
            AumidKeywords = new[] { "qqmusic", "tencent.qqmusic", "qqmusicup" },
            ProcessKeywords = new[] { "qqmusic" },
            ReportsProgress = true,
            TitleSeparator = " - ",
        };

        /// <summary>酷狗音乐：正常上报进度。</summary>
        public static readonly PlayerProfile Kugou = new()
        {
            Key = "kugou",
            DisplayName = "酷狗音乐",
            AumidKeywords = new[] { "kugou", "kgmusic" },
            ProcessKeywords = new[] { "kugou", "kgmusic" },
            ReportsProgress = true,
            TitleSeparator = " - ",
        };

        /// <summary>Spotify：正常上报进度。</summary>
        public static readonly PlayerProfile Spotify = new()
        {
            Key = "spotify",
            DisplayName = "Spotify",
            AumidKeywords = new[] { "spotify" },
            ProcessKeywords = new[] { "spotify" },
            ReportsProgress = true,
            TitleSeparator = " - ",
        };

        /// <summary>未知播放器：默认乐观假设（采信上报的时间轴）。</summary>
        public static readonly PlayerProfile Unknown = new()
        {
            Key = "unknown",
            DisplayName = "未知播放器",
            ReportsProgress = true,
        };

        private static readonly PlayerProfile[] All = { Netease, QQMusic, Kugou, Spotify };

        /// <summary>按 AUMID 判定播放器画像。</summary>
        public static PlayerProfile FromAumid(string? aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid))
            {
                return Unknown;
            }

            string lower = aumid.ToLowerInvariant();
            foreach (var profile in All)
            {
                foreach (var keyword in profile.AumidKeywords)
                {
                    if (lower.Contains(keyword, StringComparison.Ordinal))
                    {
                        return profile;
                    }
                }
            }

            return Unknown;
        }

        /// <summary>按进程名判定播放器画像（窗口标题辅助同步时用）。</summary>
        public static PlayerProfile FromProcess(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return Unknown;
            }

            string lower = processName.ToLowerInvariant();
            foreach (var profile in All)
            {
                foreach (var keyword in profile.ProcessKeywords)
                {
                    if (lower.Contains(keyword, StringComparison.Ordinal))
                    {
                        return profile;
                    }
                }
            }

            return Unknown;
        }

        /// <summary>该播放器是否需要用"窗口标题"做辅助同步（不上报进度的才需要）。</summary>
        public static bool NeedsWindowAssist(PlayerProfile profile) => !profile.ReportsProgress;
    }
}
