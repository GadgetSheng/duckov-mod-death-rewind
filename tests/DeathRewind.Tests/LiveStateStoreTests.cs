using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class LiveStateStoreTests
    {
        [Fact]
        public void Snapshot_IsDeepCopy()
        {
            var store = new LiveStateStore();
            var player = new PlayerState { Health = 50f, Alive = true };
            player.BagPayload = new byte[] { 1, 2, 3 };
            store.SetPlayer(player);

            var snapshot = store.Snapshot();

            snapshot.GetPlayer().Health = 10f;
            snapshot.GetPlayer().BagPayload[0] = 99;

            Assert.Equal(50f, store.GetPlayer().Health);
            Assert.Equal(1, store.GetPlayer().BagPayload[0]);
        }

        [Fact]
        public void GetEnemy_MissingKey_ReturnsNull()
        {
            var store = new LiveStateStore();
            store.SetEnemy("enemy:1", new EnemyState { Health = 30f, Alive = true });
            Assert.True(store.GetEnemy("enemy:1").Alive);
            Assert.Null(store.GetEnemy("enemy:999"));
        }

        [Fact]
        public void ReplaceFrom_OverwritesLiveStateWithAnchor()
        {
            var store = new LiveStateStore();
            store.SetPlayer(new PlayerState { Health = 10f });
            store.SetEnemy("enemy:1", new EnemyState { Health = 5f });

            var anchor = new Anchor
            {
                Player = new PlayerState { Health = 88f },
                Enemies = new System.Collections.Generic.Dictionary<string, EnemyState>
                {
                    ["enemy:2"] = new EnemyState { Health = 44f },
                },
            };
            store.ReplaceFrom(anchor);

            Assert.Equal(88f, store.GetPlayer().Health);
            Assert.Null(store.GetEnemy("enemy:1"));
            Assert.NotNull(store.GetEnemy("enemy:2"));
        }

        [Fact]
        public void RemoveEnemy_RemovesOnlyTargetedKey()
        {
            var store = new LiveStateStore();
            store.SetEnemy("enemy:1", new EnemyState { Health = 30f, Alive = true });
            store.SetEnemy("enemy:2", new EnemyState { Health = 40f, Alive = true });

            store.RemoveEnemy("enemy:1");

            Assert.Null(store.GetEnemy("enemy:1"));
            Assert.NotNull(store.GetEnemy("enemy:2"));
            Assert.Equal(40f, store.GetEnemy("enemy:2").Health);
        }

        [Fact]
        public void RemoveEnemy_MissingKey_DoesNotThrow()
        {
            var store = new LiveStateStore();
            store.SetEnemy("enemy:1", new EnemyState { Health = 30f, Alive = true });

            store.RemoveEnemy("enemy:999");
            store.RemoveEnemy(null);

            Assert.NotNull(store.GetEnemy("enemy:1"));
        }
    }
}
