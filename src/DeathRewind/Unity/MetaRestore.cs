using System;
using System.IO;
using Saves;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 主存档的原子覆盖/回滚 + 元系统重载。
    /// 备份与旁路文件一律留在 mod 私有目录 &lt;persistentDataPath&gt;/ModRewind/，不污染游戏 Saves 目录。
    /// </summary>
    public sealed class MetaRestore
    {
        /// <summary>
        /// 把磁盘上的 Save_{slot}.sav 原子替换为 sourcePath 的内容，再触发元系统重载。
        /// 失败返回 false（调用方负责回滚）。
        /// </summary>
        public bool OverwriteFrom(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                Debug.LogError($"[DeathRewind] 覆盖主存档失败：源文件不存在 {sourcePath}");
                return false;
            }

            string targetAbs = AnchorBuilder.GetMainSavePath();
            string tmp = targetAbs + ".tmp";
            string bak = Path.Combine(AnchorBuilder.RootDir, AnchorBuilder.AtomicBackupFileName);
            try
            {
                Directory.CreateDirectory(AnchorBuilder.RootDir);
                File.Copy(sourcePath, tmp, true);
                if (File.Exists(targetAbs))
                {
                    // 覆盖式原子替换（不先删除目标，避免"主存档已删、tmp 还没移过去"的窗口）
                    try
                    {
                        File.Replace(tmp, targetAbs, bak, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(tmp, targetAbs, true);
                        File.Delete(tmp);
                        Debug.LogWarning("[DeathRewind] File.Replace 不可用，退化为覆盖写主存档");
                    }
                    catch (NotSupportedException)
                    {
                        File.Copy(tmp, targetAbs, true);
                        File.Delete(tmp);
                        Debug.LogWarning("[DeathRewind] File.Replace 不可用，退化为覆盖写主存档");
                    }
                }
                else
                {
                    File.Move(tmp, targetAbs);
                }
                SavesSystem.SetFile(SavesSystem.CurrentSlot);   // 重新 CacheFile + 触发 OnSetFile
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 覆盖主存档失败: {e.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不影响主流程 */ }
            }
        }

        /// <summary>把当前主存档备份到 mod 私有目录，返回备份路径（失败返回 null）。</summary>
        public string BackupCurrent()
        {
            try
            {
                Directory.CreateDirectory(AnchorBuilder.RootDir);
                string targetAbs = AnchorBuilder.GetMainSavePath();
                if (!File.Exists(targetAbs))
                {
                    Debug.LogError($"[DeathRewind] 备份主存档失败：主存档不存在 {targetAbs}");
                    return null;
                }
                string backup = Path.Combine(AnchorBuilder.RootDir, AnchorBuilder.BackupFileName);
                File.Copy(targetAbs, backup, true);
                return backup;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 备份主存档失败: {e.Message}");
                return null;
            }
        }
    }
}
