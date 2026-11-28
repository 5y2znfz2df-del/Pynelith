using System;
using System.Collections.Generic;
using System.Text.Json;
using Pynelith.Island.Configuration;
using Pynelith.Island.Services;
using Pynelith.Island.Services.Motion;
using Pynelith.Island.Views;
using Xunit;

namespace Pynelith.Tests
{
    /// <summary>动画档位：缓动弹簧、自动降级调度、Lottie 导出。</summary>
    public sealed class AnimationQualityTests
    {
        private const double Frame = 1.0 / 60.0;

        [Fact]
        public void Eased_ConvergesWithoutOvershoot()
        {
            var spring = new SpringChannel { Eased = true };
            spring.Configure(0.2, 0.92);
            spring.SetTarget(1);

            double elapsed = 0;
            for (int i = 0; i < 240 && !spring.IsSettled; i++)
            {
                spring.Advance(Frame);
                elapsed += Frame;
                Assert.True(spring.Value <= 1.0005, "缓动档不应产生过冲");
            }

            Assert.True(spring.IsSettled);
            Assert.InRange(spring.Value, 0.999, 1.0);
            Assert.True(elapsed < 1.0);
        }

        [Fact]
        public void Normalize_UnknownValueFallsBackToAuto()
        {
            Assert.Equal(AnimationQualityModes.Auto, AnimationQualityModes.Normalize(null));
            Assert.Equal(AnimationQualityModes.Auto, AnimationQualityModes.Normalize("  "));
            Assert.Equal(AnimationQualityModes.Auto, AnimationQualityModes.Normalize("turbo"));
            Assert.Equal(AnimationQualityModes.Balanced, AnimationQualityModes.Normalize("balanced"));
            Assert.Equal(AnimationQualityModes.Eco, AnimationQualityModes.Normalize(" ECO "));
        }

        [Fact]
        public void CeilingFor_LowVideoMemory_Downgrades()
        {
            const long Gb = 1024L * 1024 * 1024;
            Assert.Equal(AnimationProfile.Quality, AnimationHardwareProbe.CeilingFor(0));
            Assert.Equal(AnimationProfile.Eco, AnimationHardwareProbe.CeilingFor(1024L * 1024 * 1024));
            Assert.Equal(AnimationProfile.Balanced, AnimationHardwareProbe.CeilingFor(2 * Gb));
            Assert.Equal(AnimationProfile.Quality, AnimationHardwareProbe.CeilingFor(8 * Gb));
        }

        [Fact]
        public void Governor_AutoStartsFromHardwareCeiling_AndDowngradesOnSlowFrames()
        {
            var governor = new AnimationQualityGovernor();
            governor.Configure(AnimationQualityModes.Auto, 2L * 1024 * 1024 * 1024, null);

            Assert.True(governor.AutoEnabled);
            Assert.Equal(AnimationProfile.Balanced, governor.Level);

            for (int i = 0; i < 60; i++)
            {
                governor.ReportFrame(0.04, Frame);
            }

            Assert.Equal(AnimationProfile.Eco, governor.Level);
            Assert.True(governor.IsDegraded);
        }

        [Fact]
        public void Governor_PluginFloor_ClampsAutoDowngrade()
        {
            var governor = new AnimationQualityGovernor();
            governor.Configure(
                AnimationQualityModes.Auto,
                1024L * 1024 * 1024,
                AnimationProfile.Balanced);

            Assert.Equal(AnimationProfile.Balanced, governor.Level);

            for (int i = 0; i < 240; i++)
            {
                governor.ReportFrame(0.05, Frame);
            }

            Assert.Equal(AnimationProfile.Balanced, governor.Level);
        }

        [Fact]
        public void Governor_FixedProfile_IgnoresFrameReports()
        {
            var governor = new AnimationQualityGovernor();
            governor.Configure(AnimationQualityModes.Quality, 0, null);

            for (int i = 0; i < 240; i++)
            {
                governor.ReportFrame(0.05, Frame);
            }

            Assert.Equal(AnimationProfile.Quality, governor.Level);
            Assert.False(governor.AutoEnabled);
        }

        [Fact]
        public void Governor_RecoversAfterSustainedFastFrames()
        {
            var governor = new AnimationQualityGovernor();
            governor.Configure(AnimationQualityModes.Auto, 2L * 1024 * 1024 * 1024, null);

            for (int i = 0; i < 60; i++)
            {
                governor.ReportFrame(0.04, Frame);
            }

            Assert.Equal(AnimationProfile.Eco, governor.Level);

            // 快帧持续 10 秒后回弹，但不会超过硬件上限（2GB 显存 → 平衡档）
            for (int i = 0; i < 600; i++)
            {
                governor.ReportFrame(Frame, Frame);
            }

            Assert.Equal(AnimationProfile.Balanced, governor.Level);
        }

        [Fact]
        public void LottieWriter_ProducesParsableJson_WithOneKeyPerFrame()
        {
            var frames = new List<MotionFrame>
            {
                new MotionFrame(0.00, 10, 0, 180, 36, 18),
                new MotionFrame(0.05, 5, 0, 200, 60, 22),
                new MotionFrame(0.10, 0, 0, 220, 90, 30),
            };

            string json = LottieWriter.Build(frames, 260, 120, 60);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            Assert.Equal(60, root.GetProperty("fr").GetInt32());
            Assert.Equal(260, root.GetProperty("w").GetInt32());
            Assert.Equal(120, root.GetProperty("h").GetInt32());

            JsonElement layer = root.GetProperty("layers")[0];
            JsonElement shapes = layer.GetProperty("shapes")[0].GetProperty("it");

            Assert.Equal(3, shapes[0].GetProperty("s").GetProperty("k").GetArrayLength());
            Assert.Equal(3, shapes[0].GetProperty("r").GetProperty("k").GetArrayLength());
            Assert.Equal(3, layer.GetProperty("ks").GetProperty("p").GetProperty("k").GetArrayLength());
            Assert.Equal(7, root.GetProperty("op").GetInt32());
        }

        [Fact]
        public void MotionTrace_StopsAtFrameLimit_AndRaisesCompleted()
        {
            var trace = new MotionTrace { MaxFrames = 3 };
            bool completed = false;
            trace.Completed += (_, _) => completed = true;

            trace.Start();
            Assert.True(trace.Capture(0, 0, 10, 10, 5));
            Assert.True(trace.Capture(0, 0, 10, 10, 5));
            Assert.False(trace.Capture(0, 0, 10, 10, 5));

            Assert.True(completed);
            Assert.False(trace.IsRecording);
            Assert.Equal(3, trace.Stop().Count);
        }
    }
}
