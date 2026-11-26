using System;
using Pynelith.Island.Views;
using Xunit;

namespace Pynelith.Tests
{
    /// <summary>弹簧通道：收敛性、回弹幅度、时间尖峰鲁棒性。</summary>
    public sealed class SpringChannelTests
    {
        private const double Frame = 1.0 / 60.0;

        [Fact]
        public void Advance_ConvergesToTarget_InUnderOneSecond()
        {
            var spring = new SpringChannel();
            spring.Configure(0.2, 0.92);
            spring.SetTarget(1);

            double elapsed = 0;
            for (int i = 0; i < 240 && !spring.IsSettled; i++)
            {
                spring.Advance(Frame);
                elapsed += Frame;
            }

            Assert.True(spring.IsSettled);
            Assert.InRange(spring.Value, 0.995, 1.005);
            Assert.True(elapsed < 1.0, "弹簧应在 1 秒内收敛，实际 " + elapsed.ToString("0.000") + " s");
        }

        [Fact]
        public void Advance_UnderDamped_OvershootStaysWithinSixPercent()
        {
            // 与岛里详情面板同一组参数（ζ=0.70 是回弹最强的一路）
            var spring = new SpringChannel();
            spring.Configure(0.2, 0.70);
            spring.SetTarget(1);

            double peak = double.MinValue;
            for (int i = 0; i < 300; i++)
            {
                spring.Advance(Frame);
                peak = Math.Max(peak, spring.Value);
            }

            Assert.True(peak > 1.0, "欠阻尼通道应当有可见回弹");
            Assert.True(peak <= 1.06, "回弹不得超过窗口预留的 5% 余量，实际峰值 " + peak.ToString("0.0000"));
        }

        [Fact]
        public void Advance_CriticallyDamped_DoesNotOvershoot()
        {
            var spring = new SpringChannel();
            spring.Configure(0.2, 1.0);
            spring.SetTarget(1);

            for (int i = 0; i < 300; i++)
            {
                spring.Advance(Frame);
                Assert.True(spring.Value <= 1.0005, "临界阻尼不应产生过冲");
            }
        }

        [Fact]
        public void Advance_HugeTimeSpike_DoesNotDiverge()
        {
            var spring = new SpringChannel();
            spring.Configure(0.12, 0.70);
            spring.SetTarget(1);

            // 模拟调试断点 / 系统卡顿后的超大 dt（内部会钳制到 50ms）
            for (int i = 0; i < 60; i++)
            {
                spring.Advance(5.0);
            }

            Assert.InRange(spring.Value, -0.5, 1.5);
            Assert.True(spring.IsSettled || Math.Abs(spring.Velocity) < 20);
        }

        [Fact]
        public void SetTarget_KeepsVelocity_SoAnimationCanBeInterrupted()
        {
            var spring = new SpringChannel();
            spring.Configure(0.2, 0.92);
            spring.SetTarget(1);

            for (int i = 0; i < 6; i++)
            {
                spring.Advance(Frame);
            }

            double velocity = spring.Velocity;
            Assert.True(velocity > 0);

            spring.SetTarget(0);
            Assert.Equal(velocity, spring.Velocity);
            Assert.Equal(0, spring.Target);
        }

        [Fact]
        public void SnapTo_LandsImmediately()
        {
            var spring = new SpringChannel();
            spring.SetTarget(1);
            spring.Advance(Frame);

            spring.SnapTo(0);

            Assert.Equal(0, spring.Value);
            Assert.Equal(0, spring.Velocity);
            Assert.True(spring.IsSettled);
        }

        [Fact]
        public void Advance_ZeroOrNegativeDelta_IsNoOp()
        {
            var spring = new SpringChannel();
            spring.SetTarget(1);

            spring.Advance(0);
            spring.Advance(-1);

            Assert.Equal(0, spring.Value);
        }

        [Fact]
        public void Configure_ClampsInvalidParameters()
        {
            var spring = new SpringChannel();
            spring.Configure(-5, 99);

            Assert.True(spring.Response >= 0.02);
            Assert.InRange(spring.Zeta, 0.1, 1.5);
        }
    }
}
