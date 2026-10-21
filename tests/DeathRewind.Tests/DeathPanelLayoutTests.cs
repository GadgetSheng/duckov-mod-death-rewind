using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    /// <summary>
    /// 死亡结算界面注入控件组的排版约束：
    /// 回溯按钮与原生按钮并排同一行、白色文字在回溯按钮上方、箭头指向按钮、缩略图在按钮行下方。
    /// </summary>
    public class DeathPanelLayoutTests
    {
        // 1920×1080 画布；原生"继续"按钮 858×105 居中偏下（贴近真实预制体数据）
        private static RectF Bounds => new RectF(0, 0, 1920, 1080);

        private static RectF NativeButton(float xMin = 531, float yMin = 120)
            => new RectF(xMin, yMin, xMin + 858, yMin + 105);

        [Fact]
        public void RewindButton_SitsOnSameRowAsNativeButton()
        {
            var native = NativeButton();
            var r = DeathPanelLayout.Compute(native, Bounds);

            // 同一行：底边对齐、高度相同
            Assert.Equal(native.YMin, r.RewindButton.YMin, 3);
            Assert.Equal(native.Height, r.RewindButton.Height, 3);

            // 并排：两个按钮矩形不重叠
            bool overlap = r.RewindButton.XMin < native.XMax && native.XMin < r.RewindButton.XMax;
            Assert.False(overlap);
            // 间距 = ButtonGap
            float gap = r.RewindOnRight ? r.RewindButton.XMin - native.XMax : native.XMin - r.RewindButton.XMax;
            Assert.Equal(DeathPanelLayout.ButtonGap, gap, 3);
        }

        [Fact]
        public void HintText_IsAboveRewindButton_AndArrowPointsAtIt()
        {
            var r = DeathPanelLayout.Compute(NativeButton(), Bounds);

            // 文字整体在回溯按钮上方
            Assert.True(r.HintText.YMin >= r.RewindButton.YMax, "提示文字应位于回溯按钮上方");
            // 箭头夹在文字与按钮之间，且水平居中于回溯按钮
            Assert.True(r.Arrow.YMin >= r.RewindButton.YMax, "箭头应位于回溯按钮上方");
            Assert.True(r.Arrow.YMax <= r.HintText.YMin, "箭头应位于提示文字下方");
            Assert.Equal(r.RewindButton.CenterX, r.Arrow.CenterX, 2);
            Assert.Equal(r.RewindButton.CenterX, r.HintText.CenterX, 2);
        }

        [Fact]
        public void Preview_SitsBelowButtonRow_AndDoesNotOverlapButtons()
        {
            var r = DeathPanelLayout.Compute(NativeButton(), Bounds);

            Assert.True(r.Preview.YMax <= r.RewindButton.YMin, "缩略图应在按钮行下方");
            Assert.True(r.Preview.Width > 0 && r.Preview.Height > 0, "应有可显示的缩略图");
        }

        [Fact]
        public void Preview_GrowsWhenMoreVerticalSpaceIsAvailable()
        {
            // 按钮行抬高 → 下方空间变大 → 缩略图更大
            var low = DeathPanelLayout.Compute(NativeButton(yMin: 60), Bounds);
            var high = DeathPanelLayout.Compute(NativeButton(yMin: 600), Bounds);

            Assert.True(high.Preview.Height > low.Preview.Height, "空间更大时缩略图应更大");
            Assert.True(high.Preview.Height <= DeathPanelLayout.MaxPreviewHeight);
        }

        [Fact]
        public void Preview_StaysInsideCanvas()
        {
            var r = DeathPanelLayout.Compute(NativeButton(yMin: 40), Bounds);

            Assert.True(r.Preview.YMin >= Bounds.YMin, "缩略图不应越出画布下边界");
            Assert.True(r.Preview.XMin >= Bounds.XMin, "缩略图不应越出画布左边界");
            Assert.True(r.Preview.XMax <= Bounds.XMax, "缩略图不应越出画布右边界");
        }

        [Fact]
        public void RowStaysOnScreen_WhenNativeButtonIsAtEdge()
        {
            // 原生按钮贴到画布最左 → 回溯按钮排到它右侧，整行仍在画布内
            var native = NativeButton(xMin: 0);
            var r = DeathPanelLayout.Compute(native, Bounds);

            Assert.True(r.RewindButton.XMax <= Bounds.XMax, "并排后整行应仍在画布内");
            Assert.True(r.RewindButton.XMin >= Bounds.XMin);
        }

        [Fact]
        public void RowStaysOnScreen_WhenNativeButtonIsAtRightEdge()
        {
            var native = NativeButton(xMin: 1062);   // 858 宽 → 右边缘 1920
            var r = DeathPanelLayout.Compute(native, Bounds);

            Assert.True(r.RewindButton.XMin >= Bounds.XMin, "并排后整行应仍在画布内");
            Assert.True(r.RewindButton.XMax <= Bounds.XMax);
        }

        [Fact]
        public void Preview_ShrinksInsteadOfOverflow_WhenSpaceIsTight()
        {
            // 按钮行几乎贴着画布底部 → 缩略图收缩而不是越界
            var native = new RectF(531, 0, 1389, 105);
            var r = DeathPanelLayout.Compute(native, Bounds);

            Assert.True(r.Preview.Height >= 0f);
            Assert.True(r.Preview.YMin >= Bounds.YMin, "空间不足时也不应越界");
            Assert.True(r.PreviewSpaceShortfall > 0f, "空间不足应被记录");
        }
    }
}
