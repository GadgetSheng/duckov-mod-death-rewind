using System;

namespace DeathRewind.Core
{
    /// <summary>
    /// 复活冷却计时（纯逻辑）。
    ///
    /// 语义：复活**成功**后开始计时，冷却期内不能再用复活。
    /// 时间基准是 Mod 的有效 tick（不受 <c>timeScale</c> 影响；死亡裁决冻结期间暂停），
    /// 因此暂停/结算不会偷跑冷却。
    /// </summary>
    public sealed class RewindCooldown
    {
        /// <summary>默认冷却时长（秒）。</summary>
        public const double DefaultDuration = 60;

        private double _duration = DefaultDuration;
        private double _lastUsedAt = double.NaN;

        /// <summary>
        /// 冷却是否启用。**增强模式关闭时置 false**：保持旧行为（随时都能复活）。
        /// 关闭时 <see cref="Start"/> 不记录、<see cref="Remaining"/> 恒为 0。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>冷却时长（秒）。</summary>
        public double Duration => _duration;

        /// <summary>是否处于冷却中（已经用过一次复活且未到点）。</summary>
        public bool IsCooling(double now) => !double.IsNaN(_lastUsedAt) && now < _lastUsedAt + _duration;

        /// <summary>剩余冷却秒数（&lt;= 0 表示就绪）。</summary>
        public double Remaining(double now)
        {
            if (!Enabled) return 0;
            if (double.IsNaN(_lastUsedAt)) return 0;
            double left = _lastUsedAt + _duration - now;
            return left > 0 ? left : 0;
        }

        /// <summary>是否可用（冷却结束或从未使用过）。</summary>
        public bool IsReady(double now) => Remaining(now) <= 0;

        /// <summary>开始一次冷却计时（复活成功后调用）。</summary>
        public void Start(double now)
        {
            if (!Enabled) return;
            _lastUsedAt = now;
        }

        /// <summary>
        /// 调整冷却时长。冷却进行中时按新时长重算结束时刻
        /// （从"上次使用"算起，而不是从当前时刻算起）。
        /// </summary>
        public void SetDuration(double seconds)
        {
            if (seconds <= 0) seconds = DefaultDuration;
            _duration = seconds;
        }

        /// <summary>清除冷却（换关卡）。</summary>
        public void Reset()
        {
            _lastUsedAt = double.NaN;
        }
    }
}
