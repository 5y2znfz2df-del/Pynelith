using System;
using Pynelith.Island.Configuration;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 动画档位调度器：把「用户选择 + 硬件能力 + 实时帧耗时 + 插件诉求」合成一个最终档位。
    ///
    /// 规则：
    /// - 用户选固定档位（极致 / 平衡 / 省电）时完全听用户的，不自动调整；
    /// - 「自动」档从硬件显存容量决定的档位起步（低显存如 MX330 直接进平衡档）；
    /// - 运行期按实际帧耗时降级 / 回弹：连续慢帧立即降一档，连续长时间余量充足才升回；
    /// - 动画皮肤插件声明的档位是「下限」，任何自动降级都不会让画质低于它。
    /// </summary>
    public sealed class AnimationQualityGovernor
    {
        /// <summary>帧耗时超过目标帧预算的这个倍数即视为「慢帧」。</summary>
        private const double SlowFactor = 1.55;

        /// <summary>帧耗时低于目标帧预算的这个倍数即视为「余量充足」。</summary>
        private const double FastFactor = 1.06;

        /// <summary>连续多少慢帧后降一档（60fps 下约 1 秒）。</summary>
        private const int SlowFramesToDowngrade = 60;

        /// <summary>连续多少快帧后才升一档（60fps 下约 10 秒），避免来回抖动。</summary>
        private const int FastFramesToUpgrade = 600;

        private int _slowStreak;
        private int _fastStreak;

        /// <summary>配置里的原始档位键（Auto / Quality / Balanced / Eco）。</summary>
        public string ConfiguredKey { get; private set; } = AnimationQualityModes.Auto;

        /// <summary>当前实际生效档位。</summary>
        public AnimationProfile Level { get; private set; } = AnimationProfile.Quality;

        /// <summary>自动档允许达到的最高画质（由硬件显存决定）。</summary>
        public AnimationProfile HardwareCeiling { get; private set; } = AnimationProfile.Quality;

        /// <summary>插件声明的质量下限（数值越小画质越高），null 表示没有声明。</summary>
        public AnimationProfile? PluginFloor { get; private set; }

        /// <summary>是否处于「自动」档。</summary>
        public bool AutoEnabled => ConfiguredKey == AnimationQualityModes.Auto;

        /// <summary>是否低于最高画质（用于岛上那个不打扰的小提示）。</summary>
        public bool IsDegraded => Level != AnimationProfile.Quality;

        /// <summary>档位发生变化时触发（UI 线程）。</summary>
        public event EventHandler? LevelChanged;

        /// <summary>
        /// 重新读取配置与硬件。启动时调用一次，设置里改档位时再调用。
        /// </summary>
        public void Configure(string? configuredKey, long videoMemoryBytes, AnimationProfile? pluginFloor)
        {
            ConfiguredKey = AnimationQualityModes.Normalize(configuredKey);
            HardwareCeiling = AnimationHardwareProbe.CeilingFor(videoMemoryBytes);
            PluginFloor = pluginFloor;
            _slowStreak = 0;
            _fastStreak = 0;

            if (AnimationQualityModes.TryParseFixed(ConfiguredKey, out AnimationProfile fixedProfile))
            {
                Apply(fixedProfile);
            }
            else
            {
                Apply(HardwareCeiling);
            }
        }

        /// <summary>插件加载 / 启用状态变化后更新其声明的质量下限。</summary>
        public void SetPluginFloor(AnimationProfile? floor)
        {
            if (PluginFloor == floor)
            {
                return;
            }

            PluginFloor = floor;

            // 下限被抬高时立即升级；被放宽时交给运行期慢慢回弹，避免画面来回跳
            if (floor is AnimationProfile required && (int)required < (int)Level)
            {
                Apply(required);
            }
        }

        /// <summary>
        /// 每渲染一帧上报一次耗时。只有「自动」档会据此调整。
        /// </summary>
        /// <param name="frameSeconds">本帧实际间隔（秒）。</param>
        /// <param name="targetSeconds">目标帧间隔（秒），即当前帧率上限的倒数。</param>
        public void ReportFrame(double frameSeconds, double targetSeconds)
        {
            if (!AutoEnabled || frameSeconds <= 0 || targetSeconds <= 0)
            {
                return;
            }

            double ratio = frameSeconds / targetSeconds;

            if (ratio > SlowFactor)
            {
                _fastStreak = 0;
                _slowStreak++;
                if (_slowStreak >= SlowFramesToDowngrade)
                {
                    _slowStreak = 0;
                    if (Level < AnimationProfile.Eco)
                    {
                        Apply((AnimationProfile)((int)Level + 1));
                    }
                }

                return;
            }

            if (ratio < FastFactor)
            {
                _slowStreak = 0;
                _fastStreak++;
                if (_fastStreak >= FastFramesToUpgrade)
                {
                    _fastStreak = 0;
                    if ((int)Level > (int)HardwareCeiling)
                    {
                        Apply((AnimationProfile)((int)Level - 1));
                    }
                }

                return;
            }

            // 介于两者之间：只做衰减，不立刻清零，避免负载轻微波动就重头计数
            _slowStreak = Math.Max(0, _slowStreak - 1);
            _fastStreak = Math.Max(0, _fastStreak - 1);
        }

        private void Apply(AnimationProfile desired)
        {
            AnimationProfile next = desired;

            if (PluginFloor is AnimationProfile floor && (int)floor < (int)next)
            {
                next = floor;
            }

            if (next == Level)
            {
                return;
            }

            Level = next;
            LevelChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
