using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class ScoreStateTests
    {
        /// <summary>
        /// 开启增强模式且已用掉回溯机会的记分器——计分的两个前提：
        /// 增强模式开启 + 本关已死亡用掉过 1UP（UpsUsed > 0）。
        /// </summary>
        private static ScoreState Enhanced()
        {
            var score = new ScoreState();
            score.SetEnhanced(true);
            score.TryConsume();   // 模拟死亡回溯：用掉保底机会后击杀才计分
            return score;
        }

        [Fact]
        public void NewLevel_StartsWithOneBaseUp()
        {
            var score = new ScoreState();

            Assert.Equal(1, score.Held);
            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(0, score.UpsUsed);
        }

        [Fact]
        public void AddKill_BeforeChanceUsed_DoesNotScore()
        {
            var score = new ScoreState();
            score.SetEnhanced(true);

            // 回溯机会还没用掉：击杀不计分（不能没死就预攒）
            for (int i = 0; i < 40; i++) Assert.False(score.AddKill(KillScore.BossPoints));

            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(1, score.Held);
        }

        [Fact]
        public void AddKill_AccumulatesPoints()
        {
            var score = Enhanced();

            Assert.False(score.AddKill(KillScore.CommonPoints));   // 生物 5
            Assert.Equal(5, score.Score);
            Assert.Equal(0, score.UpsEarned);

            Assert.False(score.AddKill(KillScore.MercPoints));    // 雇佣兵 15
            Assert.Equal(20, score.Score);

            Assert.False(score.AddKill(KillScore.StormPoints));    // 风暴生物 25
            Assert.Equal(45, score.Score);

            // 45 + BOSS 55 = 100 → 这一击直接发放
            Assert.True(score.AddKill(KillScore.BossPoints));
            Assert.Equal(0, score.Score);
            Assert.Equal(1, score.UpsEarned);
        }

        [Fact]
        public void AddKill_ReachingHundred_GrantsExtraUpAndResetsScore()
        {
            var score = Enhanced();

            for (int i = 0; i < 10; i++) score.AddKill(KillScore.CommonPoints);
            Assert.Equal(50, score.Score);

            for (int i = 0; i < 9; i++) score.AddKill(KillScore.CommonPoints);
            Assert.Equal(95, score.Score);
            Assert.Equal(0, score.UpsEarned);

            bool granted = score.AddKill(KillScore.CommonPoints);

            Assert.True(granted);
            Assert.Equal(0, score.Score);
            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(1, score.Held);       // 保底已用掉，赚回 1
        }

        [Fact]
        public void AddKill_Overflow_CarriesExcessScore()
        {
            var score = Enhanced();
            for (int i = 0; i < 19; i++) score.AddKill(KillScore.CommonPoints);   // 95

            bool granted = score.AddKill(KillScore.BossPoints);   // 95+55=150 → 发放，结转 50

            Assert.True(granted);
            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(50, score.Score);
        }

        [Fact]
        public void AddKill_NoCap_KeepsGrantingEveryHundred()
        {
            var score = Enhanced();

            // 取消每关上限：4 个 BOSS（220 分）应发 2 次 1UP，剩 20 分
            for (int i = 0; i < 4; i++) score.AddKill(KillScore.BossPoints);

            Assert.Equal(2, score.UpsEarned);
            Assert.Equal(20, score.Score);
            Assert.Equal(2, score.Held);   // 保底 1（已用） + 额外 2
        }

        [Fact]
        public void AddKill_AfterUsingUp_CanEarnBack()
        {
            var score = Enhanced();   // 已用掉保底 1UP，Held=0

            for (int i = 0; i < 4; i++) score.AddKill(KillScore.BossPoints);   // 220 分 → 发 2 次

            Assert.Equal(2, score.UpsEarned);
            Assert.Equal(2, score.Held);   // 赚回来的可以再用
        }

        [Fact]
        public void AddKill_ZeroOrNegativePoints_Ignored()
        {
            var score = Enhanced();

            Assert.False(score.AddKill(0));
            Assert.False(score.AddKill(-5));

            Assert.Equal(0, score.Score);
        }

        [Fact]
        public void TryConsume_ReducesHeld()
        {
            var score = Enhanced();   // UpsUsed=1, Held=0
            score.AddKill(KillScore.BossPoints);
            score.AddKill(KillScore.BossPoints);   // +1UP → Held=1

            Assert.True(score.TryConsume());

            Assert.Equal(2, score.UpsUsed);
            Assert.Equal(0, score.Held);
        }

        [Fact]
        public void TryConsume_FailsWhenNoUpLeft()
        {
            var score = new ScoreState();

            Assert.True(score.TryConsume());   // 保底 1 次
            Assert.False(score.TryConsume());
            Assert.Equal(0, score.Held);
        }

        [Fact]
        public void ResetLevel_ClearsScoreAndUps()
        {
            var score = Enhanced();
            score.AddKill(KillScore.BossPoints);
            score.AddKill(KillScore.BossPoints);
            score.TryConsume();

            score.ResetLevel();

            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(0, score.UpsUsed);
            Assert.Equal(1, score.Held);
        }

        // —— 增强模式（默认关闭）：关闭时保持旧行为（每关一次复活、不计分） ——

        [Fact]
        public void ByDefault_NotEnhanced_NoScoreAndSingleUp()
        {
            var score = new ScoreState();
            score.TryConsume();   // 即使机会已用，模式没开也不计分

            Assert.False(score.Enhanced);
            for (int i = 0; i < 40; i++) score.AddKill(KillScore.BossPoints);

            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(0, score.Held);
        }

        [Fact]
        public void SetEnhanced_True_EnablesScoringAndExtraUps()
        {
            var score = Enhanced();

            for (int i = 0; i < 20; i++) score.AddKill(KillScore.CommonPoints);

            Assert.Equal(0, score.Score);          // 满 100 → 发放并清零
            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(1, score.Held);
        }

        [Fact]
        public void SetEnhanced_True_AfterChanceUsed_DoesNotGrantUp()
        {
            // 用户反馈的 bug：默认模式用掉唯一机会后再开增强模式，不能凭空多出一次复活
            var score = new ScoreState();
            Assert.True(score.TryConsume());
            Assert.Equal(0, score.Held);

            score.SetEnhanced(true);

            Assert.Equal(0, score.Held);            // 仍是 0，没有凭空 +1
            Assert.Equal(1, score.UpsUsed);         // 已用次数保留
        }

        [Fact]
        public void SetEnhanced_True_AfterChanceUsed_ActivatesScoring()
        {
            var score = new ScoreState();
            score.TryConsume();                     // 已用掉保底机会 → 计分门槛已满足
            score.SetEnhanced(true);

            Assert.False(score.AddKill(KillScore.CommonPoints));
            Assert.Equal(KillScore.CommonPoints, score.Score);   // 击杀立刻开始计分
            Assert.Equal(0, score.UpsEarned);
        }

        [Fact]
        public void SetEnhanced_False_KeepsEarnedUp()
        {
            var score = Enhanced();
            for (int i = 0; i < 20; i++) score.AddKill(KillScore.CommonPoints);
            Assert.Equal(1, score.UpsEarned);

            score.SetEnhanced(false);

            Assert.Equal(1, score.UpsEarned);       // 已赚到的 1UP 不因关增强而丢失
            Assert.Equal(1, score.Held);
            Assert.Equal(1, score.UpsUsed);
        }

        [Fact]
        public void SetEnhanced_ToggleOffOn_KeepsEarnedUp()
        {
            // 用户反馈的 bug：没用掉刚赚到的复活机会时关→开增强，不能少一次
            var score = Enhanced();
            for (int i = 0; i < 20; i++) score.AddKill(KillScore.CommonPoints);
            Assert.Equal(1, score.Held);

            score.SetEnhanced(false);
            score.SetEnhanced(true);

            Assert.Equal(1, score.Held);            // 机会未被吞掉
            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(1, score.UpsUsed);
        }

        [Fact]
        public void NotEnhanced_TryConsume_StillConsumesTheBaseUp()
        {
            var score = new ScoreState();

            Assert.True(score.TryConsume());
            Assert.Equal(0, score.Held);
            Assert.False(score.TryConsume());
        }
    }
}
