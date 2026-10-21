using System;

namespace DeathRewind.Core
{
    /// <summary>UI 矩形（UnityEngine-free，纯数值，供布局计算与单测使用）。</summary>
    public struct RectF
    {
        public float XMin;
        public float YMin;
        public float XMax;
        public float YMax;

        public RectF(float xMin, float yMin, float xMax, float yMax)
        {
            XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax;
        }

        public float Width => XMax - XMin;
        public float Height => YMax - YMin;
        public float CenterX => (XMin + XMax) * 0.5f;
        public float CenterY => (YMin + YMax) * 0.5f;
        public float Top => YMax;
        public float Bottom => YMin;
        public float Left => XMin;
        public float Right => XMax;

        public override string ToString()
            => $"[({XMin:0.#},{YMin:0.#})-({XMax:0.#},{YMax:0.#})]";
    }

    /// <summary>
    /// 死亡结算界面上注入控件组的排版计算（纯函数，不依赖 Unity）。
    ///
    /// 布局约定（所有坐标都在原生"继续"按钮的父容器空间里）：
    ///   · 原生"继续"按钮**保持原样**（不收窄、不重排），回溯按钮在其**下一行**：
    ///     与继续按钮同宽、水平居中对齐；
    ///   · 白色提示文字在回溯按钮**下方**，箭头夹在按钮与文字之间（向上指向按钮）；
    ///   · 锚点缩略图在屏幕（全屏 Canvas）左侧垂直居中，尺寸按左侧空白区自适应。
    /// </summary>
    public static class DeathPanelLayout
    {
        public const float ButtonGap = 16f;      // 两个按钮之间的水平间距
        public const float ButtonRowGap = 14f;   // 回溯按钮与继续按钮的垂直间距
        public const float RowBottomGap = 20f;   // 缩略图与按钮行的垂直间距
        public const float ArrowWidth = 44f;
        public const float ArrowHeight = 30f;
        public const float ArrowGap = 8f;        // 箭头底部与按钮顶部的间距
        public const float TextGap = 10f;        // 文字底部与箭头顶部的间距
        public const float TextHeight = 56f;
        public const float Margin = 24f;         // 与画布边界的最小留白
        public const float PreviewAspect = 16f / 9f;
        public const float MaxPreviewHeight = 420f;
        public const float MinPreviewHeight = 140f;
        public const float MinButtonWidth = 120f;

        public struct Result
        {
            /// <summary>变窄后的原生"继续"按钮矩形（占左半边）。</summary>
            public RectF NativeButton;
            /// <summary>回溯按钮矩形（占右半边，与原生按钮同一行）。</summary>
            public RectF RewindButton;
            /// <summary>箭头矩形（指向回溯按钮）。</summary>
            public RectF Arrow;
            /// <summary>白色提示文字矩形。</summary>
            public RectF HintText;
            /// <summary>缩略图矩形（高度为 0 表示无可用空间，不显示）。</summary>
            public RectF Preview;
            /// <summary>缩略图空间不足的量（px）；0 = 空间充足。</summary>
            public float PreviewSpaceShortfall;
        }

        /// <summary>
        /// 计算注入控件组的排版。
        /// nativeRect：原生"继续"按钮在父容器空间的矩形。
        /// canvasRect：按钮行/文字/箭头的参照边界（结算界面根节点，同一空间）。
        /// previewCanvasRect：缩略图的参照边界（全屏 Canvas，换算到同一空间）；null 时用 canvasRect。
        /// </summary>
        public static Result Compute(RectF nativeRect, RectF canvasRect, RectF? previewCanvasRect = null)
        {
            var r = new Result();
            float w = nativeRect.Width;
            float h = nativeRect.Height;
            if (w <= 0f || h <= 0f || canvasRect.Width <= 0f) return r;

            // --- 原生"继续"按钮保持原样（不收窄、不重排，避免与其父级布局组互相拉扯），
            //     回溯按钮在其下一行：同宽、水平居中 ---
            r.NativeButton = nativeRect;
            float rewindCx = nativeRect.CenterX;
            float rewindTop = nativeRect.Bottom - ButtonRowGap;
            r.RewindButton = new RectF(rewindCx - w * 0.5f, rewindTop - h, rewindCx + w * 0.5f, rewindTop);

            float bottom = r.RewindButton.Bottom;

            // --- 箭头：回溯按钮下方，向上指向按钮 ---
            float rewindCenterX = r.RewindButton.CenterX;
            r.Arrow = new RectF(rewindCenterX - ArrowWidth * 0.5f, bottom - ArrowGap - ArrowHeight,
                rewindCenterX + ArrowWidth * 0.5f, bottom - ArrowGap);

            // --- 白色提示文字：箭头下方 ---
            float textW = Math.Max(w + 120f, 300f);
            float textY = r.Arrow.YMin - TextGap - TextHeight;
            r.HintText = new RectF(rewindCenterX - textW * 0.5f, textY,
                rewindCenterX + textW * 0.5f, textY + TextHeight);

            // --- 缩略图：屏幕（全屏 Canvas）左侧、垂直居中，宽度按左侧空白区尽量放大
            //     （右缘不超过继续按钮左缘，避免盖住结算面板的进度条/奖励行） ---
            RectF pc = previewCanvasRect ?? canvasRect;
            float leftAvailW = nativeRect.Left - Margin - (pc.Left + Margin);
            float imgH = 0f;
            float imgW = 0f;
            if (leftAvailW > 0f)
            {
                imgW = leftAvailW;
                imgH = imgW / PreviewAspect;
                if (imgH > MaxPreviewHeight)
                {
                    imgH = MaxPreviewHeight;
                    imgW = imgH * PreviewAspect;
                }
            }
            if (imgH < MinPreviewHeight)
                r.PreviewSpaceShortfall = MinPreviewHeight - imgH;   // 不足的量（px）

            float imgX = pc.Left + Margin;
            float imgY = pc.CenterY - imgH * 0.5f;   // 垂直居中；无空间时退化为中线上的零高矩形
            r.Preview = new RectF(imgX, imgY, imgX + imgW, imgY + imgH);
            return r;
        }
    }
}
