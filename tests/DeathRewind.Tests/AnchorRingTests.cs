using System.Collections.Generic;
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class AnchorRingTests
    {
        private static Anchor Make(double abs, double grid)
            => new Anchor { AbsTimestamp = abs, GridTimestamp = grid };

        [Fact]
        public void PollDueGridTimestamp_FiresOncePerInterval()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            ring.ResetGridTo(0.0, 0.0);
            Assert.Null(ring.PollDueGridTimestamp());            // 原点不是边界
            ring.SetTick(60.0);
            Assert.Equal(60.0, ring.PollDueGridTimestamp().Value);
            Assert.Null(ring.PollDueGridTimestamp());
            ring.SetTick(120.0);
            Assert.Equal(120.0, ring.PollDueGridTimestamp().Value);
        }

        [Fact]
        public void AllocateId_IsMonotonic_AndInsertKeepsIt()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            int id = ring.AllocateId();
            var a = new Anchor { Id = id };
            ring.Insert(a, new List<Anchor>());
            Assert.Equal(id, a.Id);
            Assert.True(ring.AllocateId() > id);
        }

        [Fact]
        public void Insert_AssignsId_WhenNotPreallocated()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var a = new Anchor();
            ring.Insert(a, new List<Anchor>());
            Assert.True(a.Id > 0);
        }

        [Fact]
        public void Insert_EvictsOldestBeyondCapacity()
        {
            var ring = new AnchorRing(2, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(10, 10), evicted);
            ring.Insert(Make(20, 20), evicted);
            Assert.Empty(evicted);
            ring.Insert(Make(30, 30), evicted);
            Assert.Single(evicted);
            Assert.Equal(10.0, evicted[0].AbsTimestamp);
        }

        [Fact]
        public void SelectTarget_PicksNewestWithAgeAtLeastMinAge()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(0, 0), evicted);
            ring.Insert(Make(60, 60), evicted);
            ring.Insert(Make(120, 120), evicted);
            ring.Insert(Make(180, 180), evicted);
            ring.SetTick(300.0);
            var target = ring.SelectTarget();
            Assert.NotNull(target);
            Assert.Equal(180.0, target.AbsTimestamp);   // 合格者取最新
        }

        [Fact]
        public void SelectTarget_SkipsTooYoungAnchors()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(200, 200), evicted);      // age 100 < 120
            ring.Insert(Make(120, 120), evicted);      // age 180
            ring.SetTick(300.0);
            var target = ring.SelectTarget();
            Assert.NotNull(target);
            Assert.Equal(120.0, target.AbsTimestamp);
        }

        [Fact]
        public void CommitRewind_KeepsTargetSelectable_RemovesYounger()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            var older = Make(60, 60);
            var target = Make(120, 120);
            var younger = Make(180, 180);
            ring.Insert(older, evicted);
            ring.Insert(target, evicted);
            ring.Insert(younger, evicted);

            var removed = new List<Anchor>();
            ring.CommitRewind(target, removed);

            Assert.Equal(1, ring.RewindCount);
            // 目标锚点不标记 Consumed：回溯后它仍是有效历史状态，可被下一次死亡再次选中
            Assert.False(target.Consumed);
            Assert.Single(removed);
            Assert.Equal(180.0, removed[0].AbsTimestamp);

            // 下一次回退目标：环内最新的达标锚点（即刚回溯过的 target，而非更老的锚点）
            ring.SetTick(300.0);
            var next = ring.SelectTarget();
            Assert.NotNull(next);
            Assert.Equal(120.0, next.AbsTimestamp);
        }

        [Fact]
        public void CanRewind_RespectsMaxRewinds()
        {
            var ring = new AnchorRing(4, 120.0, 1, 60.0);
            var evicted = new List<Anchor>();
            var a = Make(120, 120);
            ring.Insert(a, evicted);
            ring.CommitRewind(a, new List<Anchor>());
            ring.SetTick(300.0);
            Assert.False(ring.CanRewind());
        }

        [Fact]
        public void RemoveByScene_DropsOnlyThatScene_AndReturnsRemoved()
        {
            var ring = new AnchorRing(8, 120.0, 3, 60.0);
            ring.ResetGridTo(0.0, 0.0);
            var roomOld = new Anchor { Id = ring.AllocateId(), SceneKey = "Room_A", AbsTimestamp = 10, GridTimestamp = 10 };
            var map = new Anchor { Id = ring.AllocateId(), SceneKey = "Map", AbsTimestamp = 20, GridTimestamp = 20 };
            var roomNew = new Anchor { Id = ring.AllocateId(), SceneKey = "Room_A", AbsTimestamp = 30, GridTimestamp = 30 };
            ring.Insert(roomOld, new List<Anchor>());
            ring.Insert(map, new List<Anchor>());
            ring.Insert(roomNew, new List<Anchor>());

            var removed = new List<Anchor>();
            int count = ring.RemoveByScene("Room_A", removed);

            Assert.Equal(2, count);
            Assert.Equal(2, removed.Count);
            Assert.Single(ring.Anchors);
            Assert.Equal("Map", ring.Anchors[0].SceneKey);

            // 旧房间锚点不能再被当成当前场景的合格目标
            ring.SetTick(300.0);
            Assert.Null(ring.SelectTarget("Room_A"));
            Assert.NotNull(ring.SelectTarget("Map"));
        }

        [Fact]
        public void RemoveByScene_UnknownScene_IsNoop()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var map = new Anchor { Id = ring.AllocateId(), SceneKey = "Map", AbsTimestamp = 10, GridTimestamp = 10 };
            ring.Insert(map, new List<Anchor>());

            var removed = new List<Anchor>();
            Assert.Equal(0, ring.RemoveByScene("Other", removed));
            Assert.Empty(removed);
            Assert.Single(ring.Anchors);
        }

        [Fact]
        public void SelectTargets_ReturnsAgedAnchors_OldestFirst()
        {
            var ring = new AnchorRing(8, 120.0, 3, 60.0);
            ring.ResetGridTo(0.0, 0.0);
            for (int i = 1; i <= 5; i++)
            {
                var a = new Anchor { Id = ring.AllocateId(), SceneKey = "Map", AbsTimestamp = i * 50.0, GridTimestamp = i * 50.0 };
                ring.Insert(a, new List<Anchor>());
            }
            ring.SetTick(300.0);

            // 年龄 250/200/150 达标，100/50 不达标
            var targets = ring.SelectTargets("Map", 4);
            Assert.Equal(3, targets.Count);
            Assert.Equal(50.0, targets[0].AbsTimestamp);            // 旧 → 新
            Assert.Equal(150.0, targets[targets.Count - 1].AbsTimestamp);
        }

        [Fact]
        public void SelectTargets_CapsToNewestMaxCount()
        {
            var ring = new AnchorRing(16, 120.0, 3, 60.0);
            ring.ResetGridTo(0.0, 0.0);
            for (int i = 1; i <= 8; i++)
            {
                var a = new Anchor { Id = ring.AllocateId(), SceneKey = "Map", AbsTimestamp = i * 25.0, GridTimestamp = i * 25.0 };
                ring.Insert(a, new List<Anchor>());
            }
            ring.SetTick(300.0);

            var targets = ring.SelectTargets("Map", 4);
            Assert.Equal(4, targets.Count);
            Assert.Equal(100.0, targets[0].AbsTimestamp);           // 保留最新的 4 个
            Assert.Equal(175.0, targets[targets.Count - 1].AbsTimestamp);
        }

        [Fact]
        public void SelectTargets_FiltersByScene()
        {
            var ring = new AnchorRing(8, 120.0, 3, 60.0);
            ring.ResetGridTo(0.0, 0.0);
            var map = new Anchor { Id = ring.AllocateId(), SceneKey = "Map", AbsTimestamp = 50.0, GridTimestamp = 50.0 };
            var room = new Anchor { Id = ring.AllocateId(), SceneKey = "Room_A", AbsTimestamp = 60.0, GridTimestamp = 60.0 };
            ring.Insert(map, new List<Anchor>());
            ring.Insert(room, new List<Anchor>());
            ring.SetTick(300.0);

            var inRoom = ring.SelectTargets("Room_A", 4);
            Assert.Single(inRoom);
            Assert.Equal("Room_A", inRoom[0].SceneKey);
            Assert.Single(ring.SelectTargets("Map", 4));
        }

        [Fact]
        public void GridOriginReset_KeepsExistingAnchorsSelectable()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            var a = Make(60, 60);
            ring.Insert(a, evicted);
            ring.SetTick(300.0);

            ring.ResetGridTo(60.0, 300.0);

            Assert.NotNull(ring.SelectTarget());
            Assert.Equal(60.0, ring.SelectTarget().AbsTimestamp);
            Assert.Equal(240.0, ring.CurrentTick - a.AbsTimestamp, 6);
        }
    }
}
