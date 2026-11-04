using System;

namespace DeathRewind.Core
{
    /// <summary>
    /// 积分与 1UP 记账（纯逻辑）。
    ///
    /// **增强模式默认关闭**——关闭时保持旧行为：每关只有保底的 1 次复活，不计分、
    /// 不发放额外 1UP（上限与冷却也不生效，由调用方按同一开关处理）。
    /// 开启后规则如下：
    /// · 每关保底 <see cref="BaseUps"/> 次复活；上限 <see cref="Cap"/>（1~3）；
    /// · 击杀 BOSS 得 <see cref="BossPoints"/> 分，普通生物得 <see cref="CreaturePoints"/> 分；
    /// · 累计 <see cref="PointsPerUp"/> 分发放 1 次额外复活，并把分数清零；
    /// · 已达上限时不再发放，分数封顶为 100（界面显示"已满"）；
    /// · 分数与 1UP 计数**每关重算**（<see cref="ResetLevel"/>）。
    /// </summary>
    public sealed class ScoreState
    {
        /// <summary>满多少分发放一次额外 1UP。</summary>
        public const int PointsPerUp = 100;
        /// <summary>击杀 BOSS 的得分。</summary>
        public const int BossPoints = 50;
        /// <summary>击杀普通生物的得分。</summary>
        public const int CreaturePoints = 5;
        /// <summary>每关保底的一次复活（沿用旧行为）。</summary>
        public const int BaseUps = 1;
        /// <summary>每关 1UP 上限的最小值。</summary>
        public const int MinCap = 1;
        /// <summary>每关 1UP 上限的最大值。</summary>
        public const int MaxCap = 3;

        /// <summary>每关 1UP 上限（1~3）。</summary>
        public int Cap { get; private set; } = MinCap;
        /// <summary>当前得分（0~100，达上限时封顶为 100）。</summary>
        public int Score { get; private set; }
        /// <summary>本关靠积分额外获得的 1UP 数。</summary>
        public int UpsEarned { get; private set; }
        /// <summary>本关已消耗的 1UP 数。</summary>
        public int UpsUsed { get; private set; }

        /// <summary>增强模式：开启后积分才能换 1UP、每关上限与冷却才生效。默认关闭（保持旧行为）。</summary>
        public bool Enhanced { get; private set; }

        /// <summary>实际生效的每关 1UP 上限：增强模式关闭时固定为保底 1 次（保持旧行为）。</summary>
        public int EffectiveCap => Enhanced ? Cap : BaseUps;

        /// <summary>本关可持有的 1UP 总数（保底 + 积分获得，受生效上限约束）。</summary>
        public int TotalUps => Math.Min(EffectiveCap, BaseUps + UpsEarned);
        /// <summary>当前还能复活几次。</summary>
        public int Held => Math.Max(0, TotalUps - UpsUsed);
        /// <summary>是否已经拿满本关的 1UP（不能再靠积分增加）。</summary>
        public bool AtCap => TotalUps >= Cap;
        /// <summary>分数是否已满（未达上限时满 100 会立即发放并清零，因此这里只在达上限时为真）。</summary>
        public bool ScoreFull => Score >= PointsPerUp;

        /// <summary>换关卡：清空分数与 1UP 计数，并采用新的上限。</summary>
        public void ResetLevel(int cap)
        {
            Score = 0;
            UpsEarned = 0;
            UpsUsed = 0;
            SetCap(cap);
        }

        /// <summary>调整每关 1UP 上限（面板改设置时立即生效），越界夹取到 1~3。</summary>
        public void SetCap(int cap)
        {
            if (cap < MinCap) cap = MinCap;
            if (cap > MaxCap) cap = MaxCap;
            Cap = cap;
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
        /// 记一次击杀。返回 true 表示**刚好凑满 100 分、发放了一次额外 1UP**（需要播音效）。
        /// 增强模式关闭时什么都不做（不计分、不发放）。
        /// </summary>
        public bool AddKill(bool isBoss)
        {
            if (!Enhanced) return false;
            Score += isBoss ? BossPoints : CreaturePoints;
            if (Score < PointsPerUp) return false;

            if (AtCap)
            {
                // 已经拿满：不再发放，分数封顶（界面显示"已满"）
                Score = PointsPerUp;
                return false;
            }

            Score = 0;
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
