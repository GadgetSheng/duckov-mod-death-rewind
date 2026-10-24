using System.Collections.Generic;
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class AnchorRingTests
    {
        private static Anchor Make(double abs, double grid)
            => new Anchor { AbsTimestamp = abs, GridTimestamp = grid };

        /// <summary>造一条从 tickNow 往回每 stepSeconds 一个锚点的网格（年龄 = step 的整数倍）。</summary>
        private static List<Anchor> SeedGrid(AnchorRing ring, string sceneKey, double tickNow, double stepSeconds, int count)
        {
            var evicted = new List<Anchor>();
            for (int i = 0; i < count; i++)
            {
                double ts = tickNow - i * stepSeconds;
                var a = new Anchor { Id = ring.AllocateId(), SceneKey = sceneKey, AbsTimestamp = ts, GridTimestamp = ts };
                ring.Insert(a, evicted);
            }
            return evicted;
        }


        /// <summary>按"距今多少秒"铺锚点（ages 越靠前越旧）。</summary>
        private static void SeedAges(AnchorRing ring, string sceneKey, double tickNow, double[] ages)
        {
            var evicted = new List<Anchor>();
            foreach (double age in ages)
            {
                double ts = tickNow - age;
                var a = new Anchor { Id = ring.AllocateId(), SceneKey = sceneKey, AbsTimestamp = ts, GridTimestamp = ts };
                ring.Insert(a, evicted);
            }
        }

        [Fact]
        public void PollDueGridTimestamp_FiresOncePerInterval()
        {
            var ring = new AnchorRing(4, 60.0, 3, 60.0);
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
            var ring = new AnchorRing(4, 60.0, 3, 60.0);
            int id = ring.AllocateId();
            var a = new Anchor { Id = id };
            ring.Insert(a, new List<Anchor>());
            Assert.Equal(id, a.Id);
            Assert.True(ring.AllocateId() > id);
        }

        [Fact]
        public void Insert_AssignsId_WhenNotPreallocated()
        {
            var ring = new AnchorRing(4, 60.0, 3, 60.0);
            var a = new Anchor();
            ring.Insert(a, new List<Anchor>());
            Assert.True(a.Id > 0);
        }

        [Fact]
        public void Insert_EvictsOldestBeyondCapacity()
        {
            var ring = new AnchorRing(2, 60.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(10, 10), evicted);
            ring.Insert(Make(20, 20), evicted);
            Assert.Empty(evicted);
            ring.Insert(Make(30, 30), evicted);
            Assert.Single(evicted);
            Assert.Equal(10.0, evicted[0].AbsTimestamp);
        }

        [Fact]
        public void SelectWindow_KeepsOnlyAnchorsWithinMaxAge()
        {
            var ring = new AnchorRing(16, 60.0, 3, 5.0);
            SeedGrid(ring, "Map", 300.0, 10.0, 10);      // 年龄 0/10/20/.../90
            ring.SetTick(300.0);

            var window = ring.SelectWindow("Map", 60.0);
            // 年龄 ≤ 60：0/10/20/30/40/50/60 共 7 个（70/80/90 超窗）
            Assert.Equal(7, window.Count);
            Assert.Equal(240.0, window[0].AbsTimestamp);          // 旧 → 新
            Assert.Equal(300.0, window[window.Count - 1].AbsTimestamp);
        }

        [Fact]
        public void SelectByAge_PicksFourNodesAt60_50_40_30()
        {
            var ring = new AnchorRing(32, 60.0, 3, 5.0);
            SeedGrid(ring, "Map", 300.0, 5.0, 13);         // 年龄 0/5/10/.../60
            ring.SetTick(300.0);

            var nodes = ring.SelectByAge("Map", maxAge: 60.0, step: 10.0, maxCount: 4);
            Assert.Equal(4, nodes.Count);
            Assert.Equal(240.0, nodes[0].AbsTimestamp);           // 60s 前
            Assert.Equal(250.0, nodes[1].AbsTimestamp);           // 50s 前
            Assert.Equal(260.0, nodes[2].AbsTimestamp);           // 40s 前
            Assert.Equal(270.0, nodes[3].AbsTimestamp);           // 30s 前
        }

        [Fact]
        public void SelectByAge_PicksClosestAnchorWhenGridIsOffset()
        {
            var ring = new AnchorRing(32, 60.0, 3, 5.0);
            // 网格相对目标年龄错开 2.5s：年龄 57.5/52.5/47.5/42.5/37.5/32.5
            SeedAges(ring, "Map", 300.0, new double[] { 57.5, 52.5, 47.5, 42.5, 37.5, 32.5 });
            ring.SetTick(300.0);

            var nodes = ring.SelectByAge("Map", maxAge: 60.0, step: 10.0, maxCount: 4);
            Assert.Equal(4, nodes.Count);
            Assert.Equal(242.5, nodes[0].AbsTimestamp);           // 最接近 60s（57.5s 前）
            Assert.Equal(247.5, nodes[1].AbsTimestamp);           // 最接近 50s（52.5s 前）
            Assert.Equal(257.5, nodes[2].AbsTimestamp);           // 最接近 40s（42.5s 前）
            Assert.Equal(267.5, nodes[3].AbsTimestamp);           // 最接近 30s（32.5s 前）
        }

        [Fact]
        public void SelectByAge_IgnoresAnchorsOlderThanMaxAge()
        {
            var ring = new AnchorRing(32, 60.0, 3, 5.0);
            SeedGrid(ring, "Map", 300.0, 5.0, 13);         // 含年龄 65/70 的老锚点
            ring.SetTick(300.0);

            var nodes = ring.SelectByAge("Map", maxAge: 60.0, step: 10.0, maxCount: 4);
            // 最老节点必须是 60s 前那个（240），不能取到更老的 235/230
            Assert.Equal(240.0, nodes[0].AbsTimestamp);
            Assert.All(nodes, a => Assert.True(300.0 - a.AbsTimestamp <= 60.0));
        }

        [Fact]
        public void SelectByAge_ReturnsEmpty_WhenSceneTooYoung()
        {
            var ring = new AnchorRing(32, 60.0, 3, 5.0);
            SeedGrid(ring, "Map", 300.0, 5.0, 2);          // 年龄 0/5 —— 远不到 30s
            ring.SetTick(300.0);

            Assert.Empty(ring.SelectByAge("Map", maxAge: 60.0, step: 10.0, maxCount: 4));
        }

        [Fact]
        public void SelectByAge_FiltersByScene()
        {
            var ring = new AnchorRing(32, 60.0, 3, 5.0);
            SeedGrid(ring, "Map", 300.0, 5.0, 13);
            SeedGrid(ring, "Room_A", 300.0, 5.0, 13);
            ring.SetTick(300.0);

            var inRoom = ring.SelectByAge("Room_A", maxAge: 60.0, step: 10.0, maxCount: 4);
            Assert.Equal(4, inRoom.Count);
            Assert.All(inRoom, a => Assert.Equal("Room_A", a.SceneKey));
            Assert.All(ring.SelectByAge("Map", maxAge: 60.0, step: 10.0, maxCount: 4),
                       a => Assert.Equal("Map", a.SceneKey));
        }

        [Fact]
        public void SelectByAge_SkipsConsumedAnchors()
        {
            var ring = new AnchorRing(32, 60.0, 3, 5.0);
            SeedGrid(ring, "Map", 300.0, 5.0, 13);
            ring.SetTick(300.0);

            foreach (var a in ring.Anchors)
                if (300.0 - a.AbsTimestamp == 60.0) a.Consumed = true;

            var nodes = ring.SelectByAge("Map", maxAge: 60.0, step: 10.0, maxCount: 4);
            Assert.DoesNotContain(nodes, a => 300.0 - a.AbsTimestamp == 60.0);
        }

        [Fact]
        public void CommitRewind_KeepsTargetSelectable_RemovesYounger()
        {
            var ring = new AnchorRing(16, 60.0, 3, 5.0);
            var evicted = new List<Anchor>();
            var older = Make(200, 200);
            var target = Make(240, 240);
            var younger = Make(280, 280);
            ring.Insert(older, evicted);
            ring.Insert(target, evicted);
            ring.Insert(younger, evicted);

            var removed = new List<Anchor>();
            ring.CommitRewind(target, removed);

            Assert.Equal(1, ring.RewindCount);
            // 目标锚点不标记 Consumed：回溯后它仍是有效历史状态，可被下一次死亡再次选中
            Assert.False(target.Consumed);
            Assert.Single(removed);
            Assert.Equal(280.0, removed[0].AbsTimestamp);

            // 回溯后（tick 300）目标年龄 60s，仍在窗口内 → 仍可作为下一次回退目标
            ring.SetTick(300.0);
            var window = ring.SelectWindow(null, 60.0);
            Assert.Contains(target, window);
        }

        [Fact]
        public void CanRewind_RespectsMaxRewinds()
        {
            var ring = new AnchorRing(4, 60.0, 1, 60.0);
            var evicted = new List<Anchor>();
            var a = Make(240, 240);
            ring.Insert(a, evicted);
            ring.CommitRewind(a, new List<Anchor>());
            ring.SetTick(300.0);
            Assert.False(ring.CanRewind());
        }

        [Fact]
        public void RemoveByScene_DropsOnlyThatScene_AndReturnsRemoved()
        {
            var ring = new AnchorRing(8, 60.0, 3, 60.0);
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

            // 旧房间锚点不能再被当成当前场景的回溯目标
            ring.SetTick(60.0);
            Assert.Empty(ring.SelectWindow("Room_A", 60.0));
            Assert.Single(ring.SelectWindow("Map", 60.0));
        }

        [Fact]
        public void RemoveByScene_UnknownScene_IsNoop()
        {
            var ring = new AnchorRing(4, 60.0, 3, 60.0);
            var map = new Anchor { Id = ring.AllocateId(), SceneKey = "Map", AbsTimestamp = 10, GridTimestamp = 10 };
            ring.Insert(map, new List<Anchor>());

            var removed = new List<Anchor>();
            Assert.Equal(0, ring.RemoveByScene("Other", removed));
            Assert.Empty(removed);
            Assert.Single(ring.Anchors);
        }

        [Fact]
        public void GridOriginReset_KeepsExistingAnchorsSelectable()
        {
            var ring = new AnchorRing(16, 60.0, 3, 5.0);
            var evicted = new List<Anchor>();
            var a = Make(240, 240);
            ring.Insert(a, evicted);
            ring.SetTick(300.0);

            ring.ResetGridTo(240.0, 300.0);

            Assert.Single(ring.SelectWindow(null, 60.0));
            Assert.Equal(60.0, ring.CurrentTick - a.AbsTimestamp, 6);
        }
    }
}
