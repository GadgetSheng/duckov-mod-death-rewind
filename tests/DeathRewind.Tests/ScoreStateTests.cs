using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class ScoreStateTests
    {
        /// <summary>开启增强模式的记分器（积分换 1UP 只在增强模式下生效）。</summary>
        private static ScoreState Enhanced(int cap = 1)
        {
            var score = new ScoreState();
            score.SetEnhanced(true);
            score.SetCap(cap);
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
        public void AddKill_Creature_AddsFivePoints()
        {
            var score = Enhanced();

            Assert.False(score.AddKill(isBoss: false));

            Assert.Equal(5, score.Score);
            Assert.Equal(0, score.UpsEarned);
        }

        [Fact]
        public void AddKill_Boss_AddsFiftyPoints()
        {
            var score = Enhanced();

            Assert.False(score.AddKill(isBoss: true));

            Assert.Equal(50, score.Score);
        }

        [Fact]
        public void AddKill_ReachingHundred_GrantsExtraUpAndResetsScore()
        {
            var score = Enhanced(3);

            for (int i = 0; i < 10; i++) score.AddKill(isBoss: false);
            Assert.Equal(50, score.Score);

            // 10 次普通生物 = 50 分，再 10 次 = 100 分 → 满 100 发放
            for (int i = 0; i < 9; i++) score.AddKill(isBoss: false);
            Assert.Equal(95, score.Score);
            Assert.Equal(0, score.UpsEarned);

            bool granted = score.AddKill(isBoss: false);

            Assert.True(granted);
            Assert.Equal(0, score.Score);
            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(2, score.Held);       // 保底 1 + 额外 1
        }

        [Fact]
        public void AddKill_BossKillCanCrossHundred()
        {
            var score = Enhanced(3);
            score.AddKill(isBoss: true);   // 50
            score.AddKill(isBoss: true);   // 100 → 发放

            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(0, score.Score);
        }

        [Fact]
        public void AddKill_AtCap_DoesNotGrantMoreUps()
        {
            var score = Enhanced(1);      // 上限 1 = 保底 1 → 已满
            for (int i = 0; i < 20; i++) score.AddKill(isBoss: false);   // 20 × 5 = 100 分

            bool granted = score.AddKill(isBoss: false);

            Assert.False(granted);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(1, score.Held);
            Assert.Equal(ScoreState.PointsPerUp, score.Score);   // 分数封顶显示"已满"
        }

        [Fact]
        public void TryConsume_ReducesHeld()
        {
            var score = Enhanced(3);
            score.AddKill(isBoss: true);
            score.AddKill(isBoss: true);   // +1UP

            Assert.True(score.TryConsume());

            Assert.Equal(1, score.UpsUsed);
            Assert.Equal(1, score.Held);
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
        public void SetCap_ClampsToRange()
        {
            var score = new ScoreState();

            score.SetCap(0);
            Assert.Equal(1, score.Cap);

            score.SetCap(99);
            Assert.Equal(3, score.Cap);
        }

        [Fact]
        public void SetCap_RaisingCapMakesEarnedUpAvailable()
        {
            var score = Enhanced(1);          // 上限 1：加分也不发放
            score.AddKill(isBoss: true);
            score.AddKill(isBoss: true);
            Assert.Equal(0, score.UpsEarned);

            score.SetCap(3);
            score.AddKill(isBoss: false);     // 分数已满 100 → 这次发放

            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(2, score.Held);
        }

        [Fact]
        public void ResetLevel_ClearsScoreAndUps()
        {
            var score = Enhanced(3);
            score.AddKill(isBoss: true);
            score.AddKill(isBoss: true);
            score.TryConsume();

            score.ResetLevel(3);

            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(0, score.UpsUsed);
            Assert.Equal(1, score.Held);
            Assert.Equal(3, score.Cap);
        }

        [Fact]
        public void Cap_One_NeverGrantsExtraUp()
        {
            var score = Enhanced(1);
            for (int i = 0; i < 40; i++) score.AddKill(isBoss: true);

            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(1, score.Held);
        }

        // —— 增强模式（默认关闭）：关闭时保持旧行为（每关一次复活、不计分） ——

        [Fact]
        public void ByDefault_NotEnhanced_NoScoreAndSingleUp()
        {
            var score = new ScoreState();

            Assert.False(score.Enhanced);
            for (int i = 0; i < 40; i++) score.AddKill(isBoss: true);

            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(1, score.Held);
        }

        [Fact]
        public void ByDefault_SetCapHasNoEffect()
        {
            var score = new ScoreState();
            score.SetCap(3);

            Assert.Equal(3, score.Cap);          // 配置值照记
            Assert.Equal(1, score.TotalUps);      // 但生效上限仍是 1
            Assert.Equal(1, score.Held);
        }

        [Fact]
        public void SetEnhanced_True_EnablesScoringAndExtraUps()
        {
            var score = Enhanced(3);

            for (int i = 0; i < 20; i++) score.AddKill(isBoss: false);

            Assert.Equal(0, score.Score);          // 满 100 → 发放并清零
            Assert.Equal(1, score.UpsEarned);
            Assert.Equal(2, score.Held);
        }

        [Fact]
        public void SetEnhanced_False_DropsEarnedUpsAndScore()
        {
            var score = Enhanced(3);
            for (int i = 0; i < 20; i++) score.AddKill(isBoss: false);
            Assert.Equal(1, score.UpsEarned);

            score.SetEnhanced(false);

            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(0, score.Score);
            Assert.Equal(1, score.Held);
        }

        [Fact]
        public void SetEnhanced_TrueAgain_StartsFromCleanScore()
        {
            var score = Enhanced(3);
            for (int i = 0; i < 20; i++) score.AddKill(isBoss: false);
            score.SetEnhanced(false);

            score.SetEnhanced(true);

            Assert.Equal(0, score.Score);
            Assert.Equal(0, score.UpsEarned);
            Assert.Equal(1, score.Held);
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
