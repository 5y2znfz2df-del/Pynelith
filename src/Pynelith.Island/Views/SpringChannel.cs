using System;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// 单通道阻尼弹簧（Apple 风格）。
    ///
    /// 半隐式欧拉 + 固定 ≤8ms 子步：任意帧率下轨迹一致，且 ω·dt 较大时也不会振荡发散。
    /// 只改目标、不清速度 —— 动画可在任意时刻被新目标打断，并从当前速度自然接管，
    /// 这是灵动岛"点击立刻响应"手感的关键。
    /// </summary>
    public sealed class SpringChannel
    {
        /// <summary>单次积分最大子步（秒）。</summary>
        private const double MaxSubStep = 0.008;

        /// <summary>子步上限：再长的帧也最多拆成 8 步，避免 UI 线程被拖住。</summary>
        private const int MaxSubSteps = 8;

        /// <summary>收敛阈值（位置）。</summary>
        private const double SettlePosition = 0.0015;

        /// <summary>收敛阈值（速度，单位/秒）。</summary>
        private const double SettleVelocity = 0.02;

        /// <summary>当前位置。</summary>
        public double Value;

        /// <summary>当前速度（单位/秒）。</summary>
        public double Velocity;

        /// <summary>目标位置。</summary>
        public double Target;

        /// <summary>响应时长（秒）：越小越快。</summary>
        public double Response = 0.12;

        /// <summary>阻尼比：1 = 临界阻尼（不过冲），小于 1 = 带回弹。</summary>
        public double Zeta = 0.92;

        /// <summary>配置响应时长与阻尼比。</summary>
        public void Configure(double response, double zeta)
        {
            Response = Math.Max(0.02, response);
            Zeta = Math.Clamp(zeta, 0.1, 1.5);
        }

        /// <summary>改目标：保留当前速度，让新动画从当前状态自然接管。</summary>
        public void SetTarget(double target)
        {
            Target = target;
        }

        /// <summary>直接落位（不走动画），速度清零。</summary>
        public void SnapTo(double value)
        {
            Value = value;
            Target = value;
            Velocity = 0;
        }

        /// <summary>单次推进的最大步长（秒）。超过按 50ms 计算，防止调试断点 / 系统卡顿后时间跳变把弹簧踢飞。</summary>
        public const double MaxStepSeconds = 0.05;

        /// <summary>
        /// 推进 <paramref name="dt"/> 秒（内部自动拆子步），返回是否已收敛到目标。
        /// </summary>
        public bool Advance(double dt)
        {
            if (dt > 0 && !IsSettled)
            {
                if (dt > MaxStepSeconds)
                {
                    dt = MaxStepSeconds;
                }

                double omega = 2 * Math.PI / Response;
                double k = omega * omega;
                double c = 2 * Zeta * omega;

                int steps = Math.Min(MaxSubSteps, Math.Max(1, (int)Math.Ceiling(dt / MaxSubStep)));
                double h = dt / steps;

                for (int i = 0; i < steps; i++)
                {
                    double accel = -k * (Value - Target) - c * Velocity;
                    Velocity += accel * h;
                    Value += Velocity * h;
                }
            }

            return IsSettled;
        }

        /// <summary>是否已收敛：位置与速度都足够接近目标。</summary>
        public bool IsSettled
            => Math.Abs(Value - Target) < SettlePosition
               && Math.Abs(Velocity) < SettleVelocity;
    }
}
