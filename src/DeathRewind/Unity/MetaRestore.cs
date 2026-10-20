namespace DeathRewind.Unity
{
    /// <summary>
    /// 存档元信息恢复器。
    /// TODO: 后续任务实现（Task 11）。当前为最小桩。
    /// </summary>
    public sealed class MetaRestore
    {
        /// <summary>TODO: 后续任务实现 —— 用 sourcePath 覆盖当前元存档；当前返回 false。</summary>
        public bool OverwriteFrom(string sourcePath)
        {
            return false;
        }

        /// <summary>TODO: 后续任务实现 —— 备份当前元存档并返回备份路径。</summary>
        public string BackupCurrent()
        {
            return null;
        }
    }
}
