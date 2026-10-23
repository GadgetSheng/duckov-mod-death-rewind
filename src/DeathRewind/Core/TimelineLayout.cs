using System;

namespace DeathRewind.Core
{
    /// <summary>
    /// 结算界面回溯时间轴的纯几何计算（不依赖 Unity，可单测）。
    ///
    /// 布局：
    ///   · 时间轴横条贴在缩略图正下方，宽度与缩略图一致；
    ///   · 节点圆点沿横条等距排布（最左 = 最早的锚点，最右 = 最近的锚点）；
    ///   · 每个节点下方一行"距今多少秒"的小标签；
    ///   · 拖拽时把指针 x 吸附到最近的节点。
    /// </summary>
    public static class TimelineLayout
    {
        public const float StripHeight = 6f;      // 横条厚度
        public const float NodeRadius = 9f;       // 节点圆点半径
        public const float GapAbove = 22f;        // 时间轴与缩略图的间距
        public const float LabelGap = 6f;         // 标签与横条的间距
        public const float LabelHeight = 24f;     // 标签行高
        public const float LabelHalfWidth = 46f;  // 标签半宽（居中于节点）
        public const float MinPreviewWidth = 40f; // 缩略图窄于此值时不画时间轴

        public struct Result
        {
            /// <summary>时间轴横条矩形（根空间）。</summary>
            public RectF Strip;

            /// <summary>节点圆点矩形（根空间），索引 = 候选顺序（旧 → 新）。</summary>
            public RectF[] Nodes;

            /// <summary>节点标签矩形（根空间），索引与节点一致。</summary>
            public RectF[] Labels;

            /// <summary>节点中心的 x 坐标（根空间），用于拖拽吸附。</summary>
            public float[] NodeCenters;

            /// <summary>是否算出了可用布局（缩略图没有空间时为 false）。</summary>
            public bool HasLayout => Nodes != null && Nodes.Length > 0;
        }

        /// <summary>
        /// 计算时间轴布局。
        /// preview：缩略图矩形（时间轴贴在它下方）；宽度不足时不画时间轴。
        /// nodeCount：候选节点数量（1 ~ 4）。
        /// </summary>
        public static Result Compute(RectF preview, int nodeCount)
        {
            var r = new Result();
            if (nodeCount <= 0) return r;
            if (preview.Width < MinPreviewWidth) return r;

            float width = preview.Width;
            float xMin = preview.XMin;
            float stripTop = preview.YMin - GapAbove;
            float stripBottom = stripTop - StripHeight;
            r.Strip = new RectF(xMin, stripBottom, xMin + width, stripTop);

            var nodes = new RectF[nodeCount];
            var labels = new RectF[nodeCount];
            var centers = new float[nodeCount];
            float step = nodeCount > 1 ? (width - NodeRadius * 2f) / (nodeCount - 1) : 0f;

            for (int i = 0; i < nodeCount; i++)
            {
                float cx = nodeCount > 1 ? xMin + NodeRadius + i * step : xMin + width * 0.5f;
                float cy = r.Strip.CenterY;
                centers[i] = cx;
                nodes[i] = new RectF(cx - NodeRadius, cy - NodeRadius, cx + NodeRadius, cy + NodeRadius);
                labels[i] = new RectF(cx - LabelHalfWidth, stripBottom - LabelGap - LabelHeight,
                                      cx + LabelHalfWidth, stripBottom - LabelGap);
            }

            r.Nodes = nodes;
            r.Labels = labels;
            r.NodeCenters = centers;
            return r;
        }

        /// <summary>把任意 x（根空间）吸附到最近的节点索引；没有节点时返回 -1。</summary>
        public static int NearestNode(float x, float[] nodeCenters)
        {
            if (nodeCenters == null || nodeCenters.Length == 0) return -1;
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < nodeCenters.Length; i++)
            {
                float d = Math.Abs(nodeCenters[i] - x);
                if (d >= bestDist) continue;
                bestDist = d;
                best = i;
            }
            return best;
        }
    }
}
