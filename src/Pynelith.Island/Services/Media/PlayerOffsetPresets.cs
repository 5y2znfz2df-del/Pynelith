using System;
using System.Collections.Generic;

namespace Pynelith.Island.Services.Media
{
    /// <summary>
    /// 播放器进度对齐预设。
    ///
    /// 网易云这类不上报进度的播放器，0 点只能靠事件推算，
    /// 于是不同音频版本（无损 / SQ / 不同母带）之间会有固定偏差。
    /// 这里把"某个播放器 + 某个音频版本"的偏移量登记成预设，
    /// 用户在设置页一键套用，不用每次都手动微调。
    ///
    /// 预设是可增补的：内置几条常见版本，其余留给用户自己在设置页调好并保存。
    /// </summary>
    public sealed class PlayerOffsetPreset
    {
        /// <summary>预设标识（写入配置文件）。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>播放器 key（对应 <see cref="PlayerProfile.Key"/>）。</summary>
        public string PlayerKey { get; init; } = string.Empty;

        /// <summary>音频版本描述，例如"无损 (FLAC)"、"SQ 高品质"。</summary>
        public string Variant { get; init; } = string.Empty;

        /// <summary>进度修正量（毫秒，正值 = 播放器进度偏快，需要把进度往后调）。</summary>
        public int ProgressOffsetMs { get; init; }

        /// <summary>显示名。</summary>
        public string DisplayName => $"{PlayerOffsetPresets.PlayerName(PlayerKey)} · {Variant}";
    }

    /// <summary>
    /// 进度偏移预设库。
    ///
    /// 偏移的含义：把推算出来的播放位置加上这个值。
    /// 歌词对齐用的是同一个位置，所以这个偏移会同时修正进度条与歌词。
    /// </summary>
    public static class PlayerOffsetPresets
    {
        /// <summary>内置预设。</summary>
        public static readonly IReadOnlyList<PlayerOffsetPreset> BuiltIn = new[]
        {
            new PlayerOffsetPreset
            {
                Id = "netease-default",
                PlayerKey = "netease",
                Variant = "标准音质",
                ProgressOffsetMs = 0,
            },
            new PlayerOffsetPreset
            {
                Id = "netease-lossless",
                PlayerKey = "netease",
                Variant = "无损 (FLAC)",
                // 无损版本开头常常多一段渐入，事件触发更晚，进度需要往后推一点。
                ProgressOffsetMs = 400,
            },
            new PlayerOffsetPreset
            {
                Id = "netease-sq",
                PlayerKey = "netease",
                Variant = "SQ 高品质",
                ProgressOffsetMs = 250,
            },
        };

        /// <summary>按播放器 key 取预设。</summary>
        public static IEnumerable<PlayerOffsetPreset> ForPlayer(string playerKey)
        {
            foreach (var preset in BuiltIn)
            {
                if (string.Equals(preset.PlayerKey, playerKey, StringComparison.OrdinalIgnoreCase))
                {
                    yield return preset;
                }
            }
        }

        /// <summary>按 id 查预设。</summary>
        public static PlayerOffsetPreset? Find(string id)
        {
            foreach (var preset in BuiltIn)
            {
                if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return preset;
                }
            }

            return null;
        }

        /// <summary>播放器 key → 显示名。</summary>
        public static string PlayerName(string playerKey)
        {
            foreach (var profile in new[] { PlayerProfiles.Netease, PlayerProfiles.QQMusic, PlayerProfiles.Kugou, PlayerProfiles.Spotify })
            {
                if (string.Equals(profile.Key, playerKey, StringComparison.OrdinalIgnoreCase))
                {
                    return profile.DisplayName;
                }
            }

            return "未知播放器";
        }
    }
}
