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

        public static RectF FromCenter(float cx, float cy, float w, float h)
            => new RectF(cx - w * 0.5f, cy - h * 0.5f, cx + w * 0.5f, cy + h * 0.5f);

        public float Width => XMax - XMin;
        public float Height => YMax - YMin;
        public float CenterX => (XMin + XMax) * 0.5f;
        public float CenterY => (YMin + YMax) * 0.5f;

        public override string ToString()
            => $"[({XMin:0.#},{YMin:0.#})-({XMax:0.#},{YMax:0.#})]";
    }

    /// <summary>
    /// 死亡结算界面上注入控件组的排版计算（纯函数，不依赖 Unity）。
    ///
    /// 布局约定：
    ///   · 回溯按钮与原生"继续"按钮同一行（底边对齐），并排；
    ///   · 白色提示文字在回溯按钮正上方，箭头夹在文字与按钮之间（指向按钮）；
    ///   · 锚点缩略图在按钮行下方，按剩余竖向空间尽量放大。
    /// </summary>
    public static class DeathPanelLayout
    {
        public const float ButtonGap = 32f;      // 两个按钮之间的水平间距
        public const float RowBottomGap = 20f;   // 缩略图与按钮行的垂直间距
        public const float ArrowWidth = 44f;
        public const float ArrowHeight = 30f;
        public const float ArrowGap = 8f;        // 箭头底部与按钮顶部的间距
        public const float TextGap = 10f;        // 文字底部与箭头顶部的间距
        public const float TextHeight = 56f;
        public const float Margin = 24f;         // 与结算界面边界的最小留白
        public const float PreviewAspect = 16f / 9f;
        public const float MaxPreviewHeight = 420f;
        public const float MinPreviewHeight = 140f;

        public struct Result
        {
            /// <summary>回溯按钮是否排在原生按钮右侧。</summary>
            public bool RewindOnRight;
            /// <summary>回溯按钮矩形。</summary>
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
        /// nativeButton：原生"继续"按钮在容器本地空间的矩形。
        /// bounds：结算界面（画布）在容器本地空间的边界。
        /// </summary>
        public static Result Compute(RectF nativeButton, RectF bounds)
        {
            var r = new Result();
            float nativeW = nativeButton.Width;
            float nativeH = nativeButton.Height;
            if (nativeW <= 0f || nativeH <= 0f || bounds.Width <= 0f) return r;

            // 回溯按钮：与原生按钮同高，宽度约为原生按钮的一半（标签比"继续"短）
            float rewindW = Clamp(nativeW * 0.5f, 200f, 460f);
            float maxRewindW = bounds.Width - 2f * Margin - nativeW - ButtonGap;
            if (maxRewindW > 0f) rewindW = Math.Min(rewindW, Math.Max(maxRewindW, 160f));

            // 选边：并排后整行中心更靠近画面中心的一侧
            float centerXIfRight = (nativeButton.XMin + nativeButton.XMax + ButtonGap + rewindW) * 0.5f;
            float centerXIfLeft = (nativeButton.XMin - ButtonGap - rewindW + nativeButton.XMax) * 0.5f;
            r.RewindOnRight = Math.Abs(centerXIfRight - bounds.CenterX) <= Math.Abs(centerXIfLeft - bounds.CenterX);

            float rewindX = r.RewindOnRight
                ? nativeButton.XMax + ButtonGap
                : nativeButton.XMin - ButtonGap - rewindW;
            float rewindY = nativeButton.YMin;   // 底边对齐 → 同一行
            r.RewindButton = new RectF(rewindX, rewindY, rewindX + rewindW, rewindY + nativeH);

            float rewindCenterX = r.RewindButton.CenterX;
            float rewindTop = r.RewindButton.YMax;

            // 箭头：文字与按钮之间，向下指向回溯按钮
            r.Arrow = new RectF(rewindCenterX - ArrowWidth * 0.5f, rewindTop + ArrowGap,
                rewindCenterX + ArrowWidth * 0.5f, rewindTop + ArrowGap + ArrowHeight);

            // 白色提示文字：回溯按钮正上方
            float textW = Math.Max(rewindW + 120f, 300f);
            float textY = rewindTop + ArrowGap + ArrowHeight + TextGap;
            r.HintText = new RectF(rewindCenterX - textW * 0.5f, textY,
                rewindCenterX + textW * 0.5f, textY + TextHeight);

            // 缩略图：按钮行下方，按剩余空间尽量放大
            float rowBottom = Math.Min(nativeButton.YMin, rewindY);
            float available = rowBottom - bounds.YMin - Margin - RowBottomGap;
            float imgH = 0f;
            float imgW = 0f;
            if (available > 0f)
            {
                imgH = Math.Min(available, MaxPreviewHeight);
                imgW = imgH * PreviewAspect;
                if (imgW > bounds.Width - 2f * Margin)
                {
                    imgW = Math.Max(bounds.Width - 2f * Margin, 0f);
                    imgH = imgW / PreviewAspect;
                }
            }
            if (imgH < MinPreviewHeight)
                r.PreviewSpaceShortfall = MinPreviewHeight - available;   // 不足的量（px），>0 表示空间不够
            float imgX = Clamp(rewindCenterX - imgW * 0.5f,
                bounds.XMin + Margin, bounds.XMax - Margin - imgW);
            float imgY = rowBottom - RowBottomGap - imgH;
            if (imgH <= 0f)
            {
                // 没有可用空间：高度为 0 的占位矩形，且不越出画布
                imgY = Math.Max(rowBottom - RowBottomGap, bounds.YMin);
            }
            r.Preview = new RectF(imgX, imgY, imgX + imgW, imgY + imgH);
            return r;
        }

        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);
    }
}
