using System;
using Microsoft.Win32;

namespace Pynelith.Island.Configuration
{
    /// <summary>
    /// 动画质量档位。数值越大越省（用于比较与逐级降级）。
    /// </summary>
    public enum AnimationProfile
    {
        /// <summary>极致质感：8ms 子步积分，全部形变通道都跑弹簧。</summary>
        Quality = 0,

        /// <summary>平衡：16ms 子步，次要形变改走缓动、衰减类动画关闭。</summary>
        Balanced = 1,

        /// <summary>省电 / 弱 GPU：完全不做子步积分，弹簧换成指数缓动。</summary>
        Eco = 2,
    }

    /// <summary>
    /// 一个档位对应的具体渲染参数。窗口只读这里，不再自己散落判断档位。
    /// </summary>
    public readonly struct AnimationTuning
    {
        public AnimationTuning(
            double subStepSeconds,
            bool easedAll,
            bool easeSecondary,
            bool allowSiriGlow,
            int maxFps,
            int glassFrameRate)
        {
            SubStepSeconds = subStepSeconds;
            EasedAll = easedAll;
            EaseSecondary = easeSecondary;
            AllowSiriGlow = allowSiriGlow;
            MaxFps = maxFps;
            GlassFrameRate = glassFrameRate;
        }

        /// <summary>弹簧积分子步（秒）。0.008 = 8ms，0.016 = 16ms。</summary>
        public double SubStepSeconds { get; }

        /// <summary>所有通道都用缓动代替弹簧（省电档）。</summary>
        public bool EasedAll { get; }

        /// <summary>次要通道（消息气泡 / Siri 透镜 / 欢迎 / 插件面板）用缓动。</summary>
        public bool EaseSecondary { get; }

        /// <summary>是否运行 Siri 透镜的光晕衰减动画（纯装饰，弱 GPU 上直接跳过）。</summary>
        public bool AllowSiriGlow { get; }

        /// <summary>动画帧率上限，取配置值与档位上限的较小值。</summary>
        public int MaxFps { get; }

        /// <summary>形变期间玻璃层的采样帧率。</summary>
        public int GlassFrameRate { get; }
    }

    /// <summary>
    /// 三档全局动画策略的字符串键、显示名与参数表。
    /// 配置里存字符串（Auto / Quality / Balanced / Eco），便于手改 config.json。
    /// </summary>
    public static class AnimationQualityModes
    {
        public const string Auto = "Auto";
        public const string Quality = "Quality";
        public const string Balanced = "Balanced";
        public const string Eco = "Eco";

        /// <summary>设置页下拉项（键, 显示名）。</summary>
        public static readonly (string Key, string Name)[] All =
        {
            (Auto, "自动"),
            (Quality, "极致质感"),
            (Balanced, "平衡"),
            (Eco, "省电"),
        };

        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Auto;
            }

            foreach (var (key, _) in All)
            {
                if (string.Equals(key, value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }

            return Auto;
        }

        public static string DisplayName(string? value)
        {
            string key = Normalize(value);
            foreach (var (k, name) in All)
            {
                if (k == key)
                {
                    return name;
                }
            }

            return key;
        }

        /// <summary>固定档位解析；Auto 需要根据硬件与实时帧率另行决定。</summary>
        public static bool TryParseFixed(string? value, out AnimationProfile profile)
        {
            switch (Normalize(value))
            {
                case Quality:
                    profile = AnimationProfile.Quality;
                    return true;
                case Balanced:
                    profile = AnimationProfile.Balanced;
                    return true;
                case Eco:
                    profile = AnimationProfile.Eco;
                    return true;
                default:
                    profile = AnimationProfile.Quality;
                    return false;
            }
        }

        public static AnimationTuning Tuning(AnimationProfile profile)
        {
            switch (profile)
            {
                case AnimationProfile.Balanced:
                    return new AnimationTuning(
                        subStepSeconds: 0.016,
                        easedAll: false,
                        easeSecondary: true,
                        allowSiriGlow: false,
                        maxFps: 60,
                        glassFrameRate: 18);
                case AnimationProfile.Eco:
                    return new AnimationTuning(
                        subStepSeconds: 0.016,
                        easedAll: true,
                        easeSecondary: true,
                        allowSiriGlow: false,
                        maxFps: 30,
                        glassFrameRate: 12);
                default:
                    return new AnimationTuning(
                        subStepSeconds: 0.008,
                        easedAll: false,
                        easeSecondary: false,
                        allowSiriGlow: true,
                        maxFps: 144,
                        glassFrameRate: 24);
            }
        }
    }

    /// <summary>
    /// 硬件探测：读注册表里显卡的专用显存容量（NVIDIA / AMD / Intel 驱动都会写）。
    /// 只用于「自动」档的初始档位，运行期再按实际帧率继续调整。
    /// 返回值 0 表示读不到（不因此降级）。
    /// </summary>
    public static class AnimationHardwareProbe
    {
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        public static long TryGetVideoMemoryBytes()
        {
            long best = 0;

            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
                if (root == null)
                {
                    return 0;
                }

                foreach (string name in root.GetSubKeyNames())
                {
                    if (!int.TryParse(name, out _))
                    {
                        continue;
                    }

                    using RegistryKey? sub = root.OpenSubKey(name);
                    if (sub == null)
                    {
                        continue;
                    }

                    best = Math.Max(best, ReadSize(sub.GetValue("HardwareInformation.qwMemorySize")));
                    best = Math.Max(best, ReadSize(sub.GetValue("HardwareInformation.MemorySize")));
                }
            }
            catch
            {
                // 注册表受限（策略 / 精简系统）时静默放弃：探测不到就不降级
            }

            return best;
        }

        /// <summary>注册表里这个值可能是 QWORD / DWORD / 字节数组，统一按字节数读取。</summary>
        private static long ReadSize(object? value)
        {
            switch (value)
            {
                case long l:
                    return l > 0 ? l : 0;
                case int i:
                    return i > 0 ? i : 0; // 老驱动的 MemorySize 单位是字节（最大 4GB）
                case uint u:
                    return u > 0 ? u : 0;
                case byte[] bytes when bytes.Length >= 8:
                    return BitConverter.ToInt64(bytes, 0);
                default:
                    return 0;
            }
        }

        /// <summary>按显存容量给出「自动」档允许的最高画质。低显存机器（如 MX330 2GB）直接落在平衡档。</summary>
        public static AnimationProfile CeilingFor(long videoMemoryBytes)
        {
            if (videoMemoryBytes <= 0)
            {
                return AnimationProfile.Quality;
            }

            const long Gb = 1024L * 1024 * 1024;
            if (videoMemoryBytes < 1536L * 1024 * 1024)
            {
                return AnimationProfile.Eco;
            }

            if (videoMemoryBytes < (long)(3.5 * Gb))
            {
                return AnimationProfile.Balanced;
            }

            return AnimationProfile.Quality;
        }
    }
}
