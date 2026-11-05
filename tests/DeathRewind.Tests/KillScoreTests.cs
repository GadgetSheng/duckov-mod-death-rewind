using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class KillScoreTests
    {
        // —— BOSS 优先级最高：即使 nameKey 属于风暴/精英族，isBoss=true 也按 BOSS 计 ——

        [Fact]
        public void BossFlag_BeatsNameKey()
        {
            Assert.Equal(KillCategory.Boss, KillScore.CategoryFor("Cname_StormBoss1", isBoss: true));
            Assert.Equal(KillCategory.Boss, KillScore.CategoryFor("Cname_Usec", isBoss: true));
            Assert.Equal(KillScore.BossPoints, KillScore.PointsFor("Cname_Usec", isBoss: true));
        }

        // —— 风暴生物：仅本体 25 分 ——

        [Fact]
        public void StormCreature_ScoresTwentyFive()
        {
            Assert.Equal(KillCategory.Storm, KillScore.CategoryFor("Cname_StormCreature", isBoss: false));
            Assert.Equal(KillScore.StormPoints, KillScore.PointsFor("Cname_StormCreature", isBoss: false));
        }

        // —— 风暴衍生体（风暴虫/风暴？/风暴机器人）不算风暴生物 → 5 分兜底 ——

        [Theory]
        [InlineData("Cname_MonsterClimb")]    // 风暴虫
        [InlineData("Cname_StormVirus")]      // 风暴？
        [InlineData("Cname_StormRobot")]      // 风暴机器人
        public void StormDerivatives_FallBackToCommon(string nameKey)
        {
            Assert.Equal(KillCategory.Common, KillScore.CategoryFor(nameKey, isBoss: false));
            Assert.Equal(KillScore.CommonPoints, KillScore.PointsFor(nameKey, isBoss: false));
        }

        // —— 雇佣兵 / 游荡者族 ——

        [Theory]
        [InlineData("Cname_Usec")]        // 雇佣兵
        [InlineData("Cname_Usec_Ice")]    // 雪地雇佣兵
        [InlineData("Cname_Raider")]      // 游荡者
        [InlineData("Cname_RaiderIce")]   // 观测者（游荡者冰原变体）
        public void MercKeys_ScoreFifteen(string nameKey)
        {
            Assert.Equal(KillCategory.Mercenary, KillScore.CategoryFor(nameKey, isBoss: false));
            Assert.Equal(KillScore.MercPoints, KillScore.PointsFor(nameKey, isBoss: false));
        }

        // —— 兜底档：其他生物 / 未知 nameKey / 空值 ——

        [Theory]
        [InlineData("Cname_Scav")]      // 拾荒者
        [InlineData("Cname_Wolf")]      // 狼
        [InlineData("Cname_Zombie")]    // ？？？
        [InlineData("Cname_NeverHeard")]
        [InlineData("")]
        [InlineData(null)]
        public void UnknownKeys_FallBackToCommon(string nameKey)
        {
            Assert.Equal(KillCategory.Common, KillScore.CategoryFor(nameKey, isBoss: false));
            Assert.Equal(KillScore.CommonPoints, KillScore.PointsFor(nameKey, isBoss: false));
        }

        // —— 积分档位常量与描述 ——

        [Fact]
        public void PointValues_MatchDesign()
        {
            Assert.Equal(55, KillScore.BossPoints);
            Assert.Equal(25, KillScore.StormPoints);
            Assert.Equal(15, KillScore.MercPoints);
            Assert.Equal(5, KillScore.CommonPoints);
            Assert.Equal(5, KillScore.StarBonus);
        }

        // —— 带星精英：所属档位基础上额外 +5 ——

        [Theory]
        [InlineData("Cname_Scav", false, KillScore.CommonPoints)]      // 精英拾荒者 5+5
        [InlineData("Cname_Usec", false, KillScore.MercPoints)]        // 精英雇佣兵 15+5
        [InlineData("Cname_StormCreature", false, KillScore.StormPoints)] // 精英风暴生物 25+5
        [InlineData("Cname_Usec", true, KillScore.BossPoints)]         // 精英 BOSS 55+5
        public void StarElite_GetsBonusOnTopOfTier(string nameKey, bool isBoss, int basePoints)
        {
            Assert.Equal(basePoints, KillScore.PointsFor(nameKey, isBoss));
            Assert.Equal(basePoints + KillScore.StarBonus, KillScore.PointsFor(nameKey, isBoss, isStarElite: true));
        }

        [Fact]
        public void StarElite_CategoryUnchanged()
        {
            // 精英只加分不换档：分类仍按 nameKey/BOSS 判定
            Assert.Equal(KillCategory.Mercenary, KillScore.CategoryFor("Cname_Usec", isBoss: false));
            Assert.Equal("雇佣兵/游荡者", KillScore.Describe("Cname_Usec", isBoss: false));
        }

        [Fact]
        public void Describe_UsesCategoryNames()
        {
            Assert.Equal("BOSS", KillScore.Describe("Cname_Scav", isBoss: true));
            Assert.Equal("风暴生物", KillScore.Describe("Cname_StormCreature", isBoss: false));
            Assert.Equal("雇佣兵/游荡者", KillScore.Describe("Cname_Raider", isBoss: false));
            Assert.Equal("生物", KillScore.Describe("Cname_Scav", isBoss: false));
        }
    }
}
