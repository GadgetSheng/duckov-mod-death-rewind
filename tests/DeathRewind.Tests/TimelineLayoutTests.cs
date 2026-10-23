using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    /// <summary>结算界面回溯时间轴的几何计算。</summary>
    public class TimelineLayoutTests
    {
        private static RectF Preview() => new RectF(0f, 100f, 400f, 325f);

        [Fact]
        public void Compute_SingleNode_CentersOnStrip()
        {
            var tl = TimelineLayout.Compute(Preview(), 1);
            Assert.True(tl.HasLayout);
            Assert.Single(tl.Nodes);
            Assert.Equal(200f, tl.NodeCenters[0], 3);
        }

        [Fact]
        public void Compute_FourNodes_SpreadAcrossStrip()
        {
            var preview = Preview();
            var tl = TimelineLayout.Compute(preview, 4);
            Assert.Equal(4, tl.Nodes.Length);
            Assert.Equal(4, tl.Labels.Length);
            for (int i = 1; i < tl.NodeCenters.Length; i++)
            {
                Assert.True(tl.NodeCenters[i] > tl.NodeCenters[i - 1]);   // 旧 → 新，左到右递增
            }
            Assert.True(tl.NodeCenters[0] > preview.XMin);
            Assert.True(tl.NodeCenters[tl.NodeCenters.Length - 1] < preview.XMax);
        }

        [Fact]
        public void Compute_SitsBelowPreview()
        {
            var preview = Preview();
            var tl = TimelineLayout.Compute(preview, 3);
            Assert.True(tl.Strip.YMax < preview.YMin);   // 时间轴贴在缩略图下方
            Assert.True(tl.Labels[0].YMax < tl.Strip.YMin);
        }

        [Fact]
        public void Compute_NoPreviewSpace_NoLayout()
        {
            var narrow = new RectF(0f, 100f, 10f, 120f);
            Assert.False(TimelineLayout.Compute(narrow, 4).HasLayout);
            Assert.False(TimelineLayout.Compute(Preview(), 0).HasLayout);
        }

        [Fact]
        public void NearestNode_SnapsToClosest()
        {
            var centers = new[] { 10f, 50f, 90f };
            Assert.Equal(0, TimelineLayout.NearestNode(0f, centers));
            Assert.Equal(1, TimelineLayout.NearestNode(45f, centers));
            Assert.Equal(2, TimelineLayout.NearestNode(200f, centers));
        }

        [Fact]
        public void NearestNode_NoNodes_ReturnsMinusOne()
        {
            Assert.Equal(-1, TimelineLayout.NearestNode(0f, null));
            Assert.Equal(-1, TimelineLayout.NearestNode(0f, new float[0]));
        }
    }
}
