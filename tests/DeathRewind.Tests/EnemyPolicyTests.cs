using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class EnemyPolicyTests
    {
        [Fact]
        public void CanDestroy_OnlyWhenModDeactivatedAndCurrentlyInactive()
        {
            // Mod 自己停用的尸体 → 允许销毁
            Assert.True(EnemyPolicy.CanDestroy(modDeactivated: true, currentlyInactive: true));

            // 游戏自己停用的（玩家离开 100m 外被 SetActiveByPlayerDistance 关掉）→ 禁止销毁
            Assert.False(EnemyPolicy.CanDestroy(modDeactivated: false, currentlyInactive: true));

            // 仍然激活着 → 不销毁
            Assert.False(EnemyPolicy.CanDestroy(modDeactivated: true, currentlyInactive: false));
        }

        [Fact]
        public void CanRemoveLateSpawned_BossIsKept()
        {
            // 普通刷出来的怪：按世界回滚语义移除
            Assert.True(EnemyPolicy.CanRemoveLateSpawned(isBossCharacter: false));

            // Boss（唯一实体，刷怪器不会再生）→ 必须保留，否则永久消失
            Assert.False(EnemyPolicy.CanRemoveLateSpawned(isBossCharacter: true));
        }
    }
}
