namespace DeathRewind.Core
{
    /// <summary>
    /// 结算界面时间轴上的一个回溯候选节点。
    /// 一个节点 = 一个可回溯的锚点（附时刻缩略图与回溯年龄）。
    /// </summary>
    public struct RewindCandidate
    {
        /// <summary>该节点对应的锚点（回溯目标）。</summary>
        public Anchor Anchor;

        /// <summary>距今多少秒（回溯年龄）。</summary>
        public double AgeSeconds;

        /// <summary>锚点时刻的缩略图（PNG 字节，可能为空）。</summary>
        public byte[] PreviewPng;

        /// <summary>该节点所属空间的场景标识（Unity 场景名 == 游戏 sceneID）。</summary>
        public string SpaceKey;

        /// <summary>该节点所属空间的显示名（如"神秘山洞"）；结算界面标注用。</summary>
        public string SpaceLabel;

        public RewindCandidate(Anchor anchor, double ageSeconds)
        {
            Anchor = anchor;
            AgeSeconds = ageSeconds;
            PreviewPng = anchor != null ? anchor.PreviewPng : null;
            SpaceKey = anchor?.SceneKey;
            SpaceLabel = SpaceKey;
        }
    }
}
