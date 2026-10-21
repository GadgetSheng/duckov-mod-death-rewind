using System.Collections.Generic;
using DeathRewind.Core;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 一次锚点构建的上下文（跨帧推进）。
    /// <para>
    /// 构建分三段：① 主线程采集（分帧摊开，避免单帧长耗时）② 后台线程纯文件 IO
    /// ③ 完成后才允许锚点进入锚点环。
    /// </para>
    /// </summary>
    public sealed class AnchorBuildContext
    {
        public int Id;
        public double AbsTimestamp;
        public double GridTimestamp;

        /// <summary>正在构建的锚点（采集过程中逐步填充）。</summary>
        public Anchor Anchor;

        /// <summary>待采集的容器键（"loot:{int}" 及其原始 int 键）。</summary>
        public List<KeyValuePair<string, int>> ContainerKeys = new List<KeyValuePair<string, int>>();

        /// <summary>容器采集游标。</summary>
        public int Cursor;

        /// <summary>采集是否已完成（可以进入后台 IO 阶段）。</summary>
        public bool CaptureDone;

        /// <summary>后台 IO 任务（纯文件操作，不触碰任何 Unity / 游戏 API）。</summary>
        public System.Threading.Tasks.Task<bool> IoTask;

        /// <summary>采集累计耗时（毫秒）。</summary>
        public long CaptureMs;

        /// <summary>后台 IO 耗时（毫秒）。</summary>
        public long IoMs;

        /// <summary>后台 IO 的错误信息（成功时为 null）。</summary>
        public string IoError;
    }
}
