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
        /// <summary>默认单次积分最大子步（秒）。</summary>
        private const double DefaultSubStepSeconds = 0.008;

        /// <summary>默认子步数量上限。</summary>
        private const int DefaultSubStepLimit = 8;

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

        /// <summary>积分子步（秒）。动画档位会改成 8ms / 16ms。</summary>
        public double SubStepSeconds = DefaultSubStepSeconds;

        /// <summary>单帧最多拆多少个子步。</summary>
        public int SubStepLimit = DefaultSubStepLimit;

        /// <summary>
        /// 缓动模式：不积分弹簧，直接按指数曲线逼近目标（省电 / 弱 GPU 档）。
        /// 没有回弹、没有速度累积，CPU 开销只有一次乘法，但保留与弹簧接近的时长手感。
        /// </summary>
        public bool Eased;

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

                if (Eased)
                {
                    AdvanceEased(dt);
                    return IsSettled;
                }

                double omega = 2 * Math.PI / Response;
                double k = omega * omega;
                double c = 2 * Zeta * omega;

                int steps = Math.Min(SubStepLimit, Math.Max(1, (int)Math.Ceiling(dt / SubStepSeconds)));
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

        /// <summary>
        /// 指数缓动推进：一步到位，不需要拆分。
        /// 时间常数取响应时长的一半左右，肉眼看与弹簧的收尾速度接近，但没有过冲。
        /// </summary>
        private void AdvanceEased(double dt)
        {
            double tau = Math.Max(0.02, Response * 0.55);
            double alpha = 1 - Math.Exp(-dt / tau);
            double delta = Target - Value;
            Value += delta * alpha;
            Velocity = delta * alpha / dt;

            if (Math.Abs(Target - Value) < SettlePosition)
            {
                Value = Target;
                Velocity = 0;
            }
        }

        /// <summary>是否已收敛：位置与速度都足够接近目标。</summary>
        public bool IsSettled
            => Math.Abs(Value - Target) < SettlePosition
               && Math.Abs(Velocity) < SettleVelocity;
    }
}
