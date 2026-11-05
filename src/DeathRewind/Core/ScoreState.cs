using System;

namespace DeathRewind.Core
{
    /// <summary>
    /// 积分与 1UP 记账（纯逻辑）。
    ///
    /// **增强模式默认关闭**——关闭时保持旧行为：每关只有保底的 1 次复活，不计分、
    /// 不发放额外 1UP（冷却也不生效，由调用方按同一开关处理）。
    /// 开启后规则如下（v1.0.14 起取消每关次数上限）：
    /// · 每关保底 <see cref="BaseUps"/> 次复活，**不限总次数**；
    /// · **计分前提是本关已死亡用掉过回溯机会**（UpsUsed > 0）——先丢机会、再靠击杀赚回；
    /// · 击杀按目标类型得分（<see cref="KillScore"/>：BOSS 55 / 风暴生物 25 / 雇佣兵·游荡者 15 / 其他 5）；
    /// · 累计 <see cref="PointsPerUp"/> 分发放 1 次额外复活；**溢出分数结转**（95+50=145 → 发放后剩 45）；
    /// · 用掉 1UP 后可靠积分再次赚回，真正的限制是复活冷却（见 <see cref="RewindCooldown"/>）；
    /// · 分数与 1UP 计数**每关重算**（<see cref="ResetLevel"/>）。
    /// </summary>
    public sealed class ScoreState
    {
        /// <summary>满多少分发放一次额外 1UP。</summary>
        public const int PointsPerUp = 100;
        /// <summary>每关保底的一次复活（沿用旧行为）。</summary>
        public const int BaseUps = 1;

        /// <summary>当前得分（0~99；满 100 立即发放 1UP 并结转溢出）。</summary>
        public int Score { get; private set; }
        /// <summary>本关靠积分额外获得的 1UP 数。</summary>
        public int UpsEarned { get; private set; }
        /// <summary>本关已消耗的 1UP 数。</summary>
        public int UpsUsed { get; private set; }

        /// <summary>增强模式：开启后积分才能换 1UP、复活冷却才生效。默认关闭（保持旧行为）。</summary>
        public bool Enhanced { get; private set; }

        /// <summary>本关可持有的 1UP 总数（保底 + 积分获得，不设上限）。</summary>
        public int TotalUps => BaseUps + UpsEarned;
        /// <summary>当前还能复活几次。</summary>
        public int Held => Math.Max(0, TotalUps - UpsUsed);

        /// <summary>换关卡：清空分数与 1UP 计数。</summary>
        public void ResetLevel()
        {
            Score = 0;
            UpsEarned = 0;
            UpsUsed = 0;
        }

        /// <summary>
        /// 切换增强模式。模式变化时经济状态重算（分数与已赚 1UP 不跨模式保留），
        /// 避免"关掉再打开"白拿一次 1UP。
        /// </summary>
        public void SetEnhanced(bool enabled)
        {
            if (Enhanced == enabled) return;
            Enhanced = enabled;
            Score = 0;
            UpsEarned = 0;
            UpsUsed = 0;
        }

        /// <summary>
        /// 记一次击杀得分。返回 true 表示**刚好凑满 100 分、发放了一次额外 1UP**（需要播音效）。
        /// 积分档位见 <see cref="KillScore"/>（BOSS 55 / 风暴 25 / 雇佣兵·游荡者 15 / 其他 5）。
        /// 不设每关上限：满 100 必发放，溢出分数结转（95+50=145 → 发放后剩 45）。
        /// 两个前提：增强模式开启，且**本关已死亡用掉过回溯机会**（UpsUsed > 0）——
        /// 1UP 还没用掉时击杀不计分（先丢机会、再靠击杀赚回，不能没死就预攒）。
        /// </summary>
        public bool AddKill(int points)
        {
            if (!Enhanced) return false;
            if (UpsUsed <= 0) return false;   // 回溯机会还没用掉 → 不计分
            if (points <= 0) return false;
            Score += points;
            if (Score < PointsPerUp) return false;

            Score -= PointsPerUp;
            UpsEarned++;
            return true;
        }

        /// <summary>消耗一次复活机会；没有可用机会时返回 false。</summary>
        public bool TryConsume()
        {
            if (Held <= 0) return false;
            UpsUsed++;
            return true;
        }
    }
}
