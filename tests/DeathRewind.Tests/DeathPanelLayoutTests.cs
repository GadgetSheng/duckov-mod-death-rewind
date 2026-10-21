using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    /// <summary>
    /// 死亡结算界面注入控件组的排版约束：
    /// 原生"继续"按钮保持原样，回溯按钮在其下一行（同宽居中）；
    /// 白色文字在回溯按钮下方、箭头向上指向按钮；缩略图在屏幕左侧垂直居中。
    /// </summary>
    public class DeathPanelLayoutTests
    {
        // 1920×1080 画布；原生"继续"按钮 858×105（贴近真实预制体数据）
        private static RectF Bounds => new RectF(0, 0, 1920, 1080);

        private static RectF NativeButton(float xMin = 531, float yMin = 120)
            => new RectF(xMin, yMin, xMin + 858, yMin + 105);

        [Fact]
        public void NativeButton_Untouched_AndRewindBelowIt()
        {
            var native = NativeButton();
            var r = DeathPanelLayout.Compute(native, Bounds);

            // 原生按钮完全不动（避免与其父级布局组互相拉扯造成位移）
            Assert.Equal(native.XMin, r.NativeButton.XMin, 3);
            Assert.Equal(native.XMax, r.NativeButton.XMax, 3);
            Assert.Equal(native.YMin, r.NativeButton.YMin, 3);
            Assert.Equal(native.YMax, r.NativeButton.YMax, 3);

            // 回溯按钮在下一行：同宽、水平居中、留一道垂直缝
            Assert.Equal(native.Width, r.RewindButton.Width, 3);
            Assert.Equal(native.CenterX, r.RewindButton.CenterX, 3);
            Assert.Equal(native.YMin - DeathPanelLayout.ButtonRowGap, r.RewindButton.YMax, 3);
        }

        [Fact]
        public void RowStaysInsideCanvas_WhenNativeButtonTouchesLeftEdge()
        {
            // 原生按钮贴到画布最左 → 右侧的回溯按钮仍要在画布内
            var native = NativeButton(xMin: 0);
            var r = DeathPanelLayout.Compute(native, Bounds);

            Assert.True(r.RewindButton.XMax <= Bounds.XMax, "并排后整行应仍在画布内");
            Assert.True(r.NativeButton.XMin >= Bounds.XMin);
        }

        [Fact]
        public void HintText_IsBelowRewindButton_AndArrowPointsUpAtIt()
        {
            var r = DeathPanelLayout.Compute(NativeButton(yMin: 500), Bounds);

            // 箭头夹在回溯按钮与文字之间：按钮底 → 箭头 → 文字
            Assert.True(r.Arrow.YMax <= r.RewindButton.YMin, "箭头应位于回溯按钮下方");
            Assert.True(r.HintText.YMax <= r.Arrow.YMin, "文字应位于箭头下方");
            // 均水平居中于回溯按钮（箭头向上指向按钮）
            Assert.Equal(r.RewindButton.CenterX, r.Arrow.CenterX, 2);
            Assert.Equal(r.RewindButton.CenterX, r.HintText.CenterX, 2);
        }

        [Fact]
        public void Preview_VerticallyCenteredOnLeft()
        {
            var native = NativeButton(yMin: 500);
            var r = DeathPanelLayout.Compute(native, Bounds);

            Assert.Equal(DeathPanelLayout.Margin, r.Preview.XMin, 3);
            Assert.Equal(Bounds.CenterY, r.Preview.CenterY, 3);
            // 右缘不超过继续按钮左缘（留一道缝），避免盖住结算面板的进度条/奖励行
            Assert.Equal(native.XMin - DeathPanelLayout.Margin, r.Preview.XMax, 3);
        }

        [Fact]
        public void Preview_Renders_WhenLeftSpaceIsSufficient()
        {
            var r = DeathPanelLayout.Compute(NativeButton(yMin: 500), Bounds);

            Assert.True(r.Preview.Width > 0 && r.Preview.Height > 0, "应有可显示的缩略图");
        }

        [Fact]
        public void Preview_GrowsWhenMoreHorizontalSpaceIsAvailable()
        {
            // 继续按钮右移 → 左侧空白区变宽 → 缩略图更大
            var narrow = DeathPanelLayout.Compute(NativeButton(xMin: 300), Bounds);
            var wide = DeathPanelLayout.Compute(NativeButton(xMin: 800), Bounds);

            Assert.True(wide.Preview.Height > narrow.Preview.Height, "左侧空间更大时缩略图应更大");
            Assert.True(wide.Preview.Height <= DeathPanelLayout.MaxPreviewHeight);
        }

        [Fact]
        public void Preview_StaysInsideCanvas()
        {
            var r = DeathPanelLayout.Compute(NativeButton(), Bounds);

            Assert.True(r.Preview.YMin >= Bounds.YMin, "缩略图不应越出画布下边界");
            Assert.True(r.Preview.XMin >= Bounds.XMin, "缩略图不应越出画布左边界");
            Assert.True(r.Preview.XMax <= Bounds.XMax, "缩略图不应越出画布右边界");
        }

        [Fact]
        public void Preview_Collapses_WhenNoLeftSpace()
        {
            // 继续按钮贴到画布最左 → 左侧没有空白区 → 缩略图收起而不是越界
            var native = NativeButton(xMin: 0);
            var r = DeathPanelLayout.Compute(native, Bounds);

            Assert.Equal(0f, r.Preview.Height, 3);
            Assert.True(r.Preview.YMin >= Bounds.YMin, "收起时也不应越界");
            Assert.True(r.PreviewSpaceShortfall > 0f, "空间不足应被记录");
        }
    }
}
