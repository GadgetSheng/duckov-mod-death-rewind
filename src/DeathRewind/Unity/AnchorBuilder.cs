using System.Collections.Generic;
using System.IO;
using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 锚点构建器。
    /// TODO: 后续任务实现（Task 8）。当前为最小桩：只维护文件路径与锚点骨架，不落盘。
    /// </summary>
    public sealed class AnchorBuilder
    {
        /// <summary>锚点文件根目录。</summary>
        public static string RootDir { get; } =
            Path.GetFullPath(Path.Combine(Application.dataPath, "../../DeathRewindAnchors"));

        /// <summary>玩家主存档备份文件名（关卡结束时必须保留）。</summary>
        public const string BackupFileName = "save_backup.sav";

        private readonly Config.ModConfig _config;
        private readonly LiveStateStore _store;
        private readonly AnchorRing _ring;

        public AnchorBuilder(Config.ModConfig config, LiveStateStore store, AnchorRing ring)
        {
            _config = config;
            _store = store;
            _ring = ring;
        }

        /// <summary>TODO: 后续任务实现 —— 删除指定锚点对应的载荷/存档文件。</summary>
        public static void DeleteAnchorFiles(Anchor anchor)
        {
            if (anchor == null) return;
            TryDelete(AnchorPayloadPath(anchor.Id));
            TryDelete(AnchorSavePath(anchor.Id));
        }

        /// <summary>TODO: 后续任务实现 —— 删除全部锚点文件，但保留 save_backup.sav。</summary>
        public static void DeleteAllAnchorFiles()
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (var file in Directory.GetFiles(RootDir))
                {
                    var name = Path.GetFileName(file);
                    if (name == BackupFileName) continue;
                    TryDelete(file);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 清理锚点文件失败: {e.Message}");
            }
        }

        /// <summary>TODO: 后续任务实现 —— 清理不对应任何存活锚点的孤儿文件（保留 save_backup.sav）。</summary>
        public static void CleanupOrphanFiles(HashSet<int> liveIds)
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (var file in Directory.GetFiles(RootDir))
                {
                    var name = Path.GetFileName(file);
                    if (name == BackupFileName) continue;
                    int id;
                    if (TryParseAnchorId(name, out id) && liveIds != null && liveIds.Contains(id)) continue;
                    TryDelete(file);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 清理孤儿锚点文件失败: {e.Message}");
            }
        }

        /// <summary>TODO: 后续任务实现 —— 构建锚点内容并落盘。当前只返回骨架。</summary>
        public Anchor Build(int id, double absTimestamp, double gridTimestamp)
        {
            var anchor = new Anchor
            {
                Id = id,
                AbsTimestamp = absTimestamp,
                GridTimestamp = gridTimestamp,
            };
            return anchor;
        }

        public static string AnchorPayloadPath(int id) => Path.Combine(RootDir, $"anchor_{id}.es3");
        public static string AnchorSavePath(int id) => Path.Combine(RootDir, $"save_{id}.sav");
        public static string BackupSavePath => Path.Combine(RootDir, BackupFileName);

        private static bool TryParseAnchorId(string fileName, out int id)
        {
            id = 0;
            var parts = fileName.Split('_');
            if (parts.Length < 2) return false;
            var tail = parts[1].Split('.');
            return int.TryParse(tail[0], out id);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 删除文件失败 {path}: {e.Message}");
            }
        }
    }
}
