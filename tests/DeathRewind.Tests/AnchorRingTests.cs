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
        public void CommitRewind_ConsumesTarget_RemovesYounger_KeepsOlder()
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
            Assert.True(target.Consumed);
            Assert.Single(removed);
            Assert.Equal(180.0, removed[0].AbsTimestamp);

            // 更早的锚点仍可作为下一次回退目标
            ring.SetTick(300.0);
            var next = ring.SelectTarget();
            Assert.NotNull(next);
            Assert.Equal(60.0, next.AbsTimestamp);
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
