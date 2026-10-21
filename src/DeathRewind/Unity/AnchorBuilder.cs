using System;
using System.Collections.Generic;
using System.IO;
using DeathRewind.Core;
using Saves;   // Saves.SavesSystem
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 按指定 Id 与时刻构建锚点内容并落盘。
    /// 所有文件位于 mod 私有目录 &lt;persistentDataPath&gt;/ModRewind/：**不写进游戏 Saves 目录**，
    /// 避免被游戏自身的存档扫描/备份逻辑看到。
    /// </summary>
    public sealed class AnchorBuilder
    {
        /// <summary>主存档备份文件名（关卡结束/清理孤儿时必须保留）。</summary>
        public const string BackupFileName = "save_backup.sav";

        /// <summary>原子替换时 File.Replace 产生的旁路备份文件名（同样留在 mod 目录内）。</summary>
        public const string AtomicBackupFileName = "atomic_replace.bak";

        /// <summary>锚点载荷文件里玩家背包的键名。</summary>
        public const string PlayerPayloadKey = "player";

        private readonly Config.ModConfig _config;
        private readonly LiveStateStore _store;
        private readonly AnchorRing _ring;

        public AnchorBuilder(Config.ModConfig config, LiveStateStore store, AnchorRing ring)
        {
            _config = config;
            _store = store;
            _ring = ring;
        }

        /// <summary>锚点文件根目录：&lt;Application.persistentDataPath&gt;/ModRewind（mod 私有数据目录）。</summary>
        public static string RootDir => Path.Combine(Application.persistentDataPath, "ModRewind");

        public Anchor Build(int id, double absTimestamp, double gridTimestamp)
        {
            // 1) 元系统刷新：让主存档反映锚点时刻的 EXP/任务/经济
            SavesSystem.CollectSaveData();
            SavesSystem.SaveFile();

            var anchor = new Anchor
            {
                Id = id,
                AbsTimestamp = absTimestamp,
                GridTimestamp = gridTimestamp,
                Player = _store.GetPlayer()?.Clone(),
                World = new WorldState { TimeOfDay = ReadWorldTime() },
            };

            foreach (var key in _store.EnemyKeys) anchor.Enemies[key] = _store.GetEnemy(key)?.Clone();
            foreach (var key in _store.ContainerKeys) anchor.Containers[key] = _store.GetContainer(key)?.Clone();
            foreach (var key in _store.DoorKeys) anchor.Doors[key] = _store.GetDoor(key)?.Clone();

            // 2) 载荷与主存档副本落盘（文件名与锚点 Id 对应）
            anchor.ContainerPayloadPath = AnchorPayloadPath(id);
            anchor.MainSaveCopyPath = AnchorSavePath(id);
            try
            {
                Directory.CreateDirectory(RootDir);
                SavePayloads(anchor);
                CopyMainSave(anchor.MainSaveCopyPath);
            }
            catch (Exception e)
            {
                // 落盘失败不抛到帧循环：阶段 A 会因"锚点主存档副本缺失"而拒绝回退
                Debug.LogError($"[DeathRewind] 锚点 {id} 落盘失败: {e.Message}");
            }
            VerifyPayloads(anchor);
            return anchor;
        }

        /// <summary>往返自检：载荷 Capture→Parse 后条目数必须一致（构建锚点时调用一次）。</summary>
        private static void VerifyPayloads(Anchor anchor)
        {
            try
            {
                var map = LevelAccess.LootBoxInventories;
                if (map != null)
                {
                    foreach (var kv in map)
                    {
                        if (kv.Value == null) continue;
                        if (!InventoryPayload.VerifyRoundTrip(kv.Value))
                            Debug.LogWarning($"[DeathRewind] 锚点 {anchor.Id} 容器 {kv.Key} 载荷往返自检失败");
                    }
                }
                var main = LevelManager.Instance?.MainCharacter;
                if (main != null && !InventoryPayload.VerifyRoundTrip(main.CharacterItem?.Inventory))
                    Debug.LogWarning($"[DeathRewind] 锚点 {anchor.Id} 玩家背包载荷往返自检失败");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 锚点 {anchor.Id} 载荷自检失败: {e.Message}");
            }
        }

        /// <summary>
        /// 写入锚点载荷：容器背包与玩家背包统一以 <b>Base64 字符串</b> 存进 ES3
        /// （不使用 byte[] 重载，避免两种文件格式并存导致读错）。
        /// </summary>
        private static void SavePayloads(Anchor anchor)
        {
            var settings = new ES3Settings
            {
                location = ES3.Location.File,
                path = anchor.ContainerPayloadPath,
            };
            try { ES3.DeleteFile(anchor.ContainerPayloadPath, settings); }
            catch (Exception e) { Debug.LogWarning($"[DeathRewind] 清理旧锚点载荷失败: {e.Message}"); }
            int saved = 0;
            foreach (var kv in anchor.Containers)
            {
                if (kv.Value?.ItemPayload == null) continue;
                ES3.Save(kv.Key, Convert.ToBase64String(kv.Value.ItemPayload), settings);
                saved++;
            }
            if (anchor.Player?.BagPayload != null)
            {
                ES3.Save(PlayerPayloadKey, Convert.ToBase64String(anchor.Player.BagPayload), settings);
                saved++;
            }
            // 没有写入任何键时不要 StoreCachedFile：ES3 会因缓存里找不到该文件而抛异常
            if (saved == 0)
            {
                Debug.LogWarning($"[DeathRewind] 锚点 {anchor.Id} 没有可落盘的载荷（容器与背包均为空）");
                return;
            }
            // ES3Settings.location = File 时，ES3.Save 直接写盘，**不需要** StoreCachedFile
            // （StoreCachedFile 只会去找缓存里的文件，文件已直接落盘时会抛 "could not be found in the cache"）
        }

        /// <summary>
        /// 从锚点载荷文件读回容器/玩家背包载荷（Base64 → 原始字节）。
        /// 内存态丢失时（例如锚点对象被重建）作为兜底来源。
        /// </summary>
        public static bool TryLoadPayloads(Anchor anchor, out Dictionary<string, byte[]> containers, out byte[] playerBag)
        {
            containers = new Dictionary<string, byte[]>();
            playerBag = null;
            if (anchor == null || string.IsNullOrEmpty(anchor.ContainerPayloadPath)) return false;
            if (!File.Exists(anchor.ContainerPayloadPath)) return false;
            try
            {
                var settings = new ES3Settings
                {
                    location = ES3.Location.File,
                    path = anchor.ContainerPayloadPath,
                };
                foreach (var kv in anchor.Containers)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    try
                    {
                        var b64 = ES3.Load<string>(kv.Key, settings);
                        if (string.IsNullOrEmpty(b64)) continue;
                        containers[kv.Key] = Convert.FromBase64String(b64);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[DeathRewind] 读取锚点载荷键 {kv.Key} 失败: {e.Message}");
                    }
                }
                try
                {
                    var playerB64 = ES3.Load<string>(PlayerPayloadKey, settings);
                    if (!string.IsNullOrEmpty(playerB64)) playerBag = Convert.FromBase64String(playerB64);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DeathRewind] 读取锚点玩家载荷失败: {e.Message}");
                }
                return containers.Count > 0 || playerBag != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 读取锚点载荷失败 {anchor.ContainerPayloadPath}: {e.Message}");
                return false;
            }
        }

        /// <summary>把当前主存档复制到锚点目录（锚点时刻的元系统快照）。</summary>
        private static void CopyMainSave(string dest)
        {
            string src = GetMainSavePath();
            if (!File.Exists(src))
            {
                Debug.LogError($"[DeathRewind] 主存档不存在，无法复制锚点副本: {src}");
                return;
            }
            try
            {
                File.Copy(src, dest, true);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 复制主存档副本失败 {src} → {dest}: {e.Message}");
            }
        }

        /// <summary>
        /// 当前主存档的绝对路径。
        /// SavesSystem.GetFilePath 在不同版本上可能返回绝对路径或相对路径，这里两种都兼容。
        /// </summary>
        public static string GetMainSavePath()
        {
            int slot = 0;
            try { slot = SavesSystem.CurrentSlot; } catch { /* 未初始化时用槽位 0 */ }

            string path = null;
            try { path = SavesSystem.GetFilePath(slot); } catch { /* 忽略：走兜底拼装 */ }

            if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path)) return path;

            if (!string.IsNullOrEmpty(path) && (path.IndexOf(Path.DirectorySeparatorChar) >= 0 || path.IndexOf('/') >= 0))
                return Path.Combine(Application.persistentDataPath, path);

            string folder = null;
            try { folder = SavesSystem.GetFullPathToSavesFolder(); } catch { /* 忽略 */ }
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(Application.persistentDataPath, "Saves");

            string fileName = path;
            if (string.IsNullOrEmpty(fileName))
            {
                try { fileName = SavesSystem.GetSaveFileName(slot); } catch { /* 忽略 */ }
                if (string.IsNullOrEmpty(fileName)) fileName = $"Save_{slot}.sav";
            }
            return Path.Combine(folder, fileName);
        }

        public static void DeleteAnchorFiles(Anchor anchor)
        {
            if (anchor == null) return;
            TryDelete(!string.IsNullOrEmpty(anchor.ContainerPayloadPath) ? anchor.ContainerPayloadPath : AnchorPayloadPath(anchor.Id));
            TryDelete(!string.IsNullOrEmpty(anchor.MainSaveCopyPath) ? anchor.MainSaveCopyPath : AnchorSavePath(anchor.Id));
        }

        /// <summary>关卡结束：清理锚点文件，但**保留** save_backup.sav（阶段 B/F 的回滚源）。</summary>
        public static void DeleteAllAnchorFiles()
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (var f in Directory.GetFiles(RootDir))
                {
                    if (IsProtectedFile(f)) continue;
                    TryDelete(f);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 清理锚点文件失败: {e.Message}");
            }
        }

        /// <summary>启动时清理孤儿文件（文件名不对应任何现存锚点），但保留 save_backup.sav。</summary>
        public static void CleanupOrphanFiles(HashSet<int> liveIds)
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (var f in Directory.GetFiles(RootDir))
                {
                    if (IsProtectedFile(f)) continue;
                    int id;
                    if (TryParseAnchorId(Path.GetFileName(f), out id) && liveIds != null && liveIds.Contains(id)) continue;
                    TryDelete(f);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 清理孤儿锚点文件失败: {e.Message}");
            }
        }

        public static string AnchorPayloadPath(int id) => Path.Combine(RootDir, $"anchor_{id}.es3");
        public static string AnchorSavePath(int id) => Path.Combine(RootDir, $"save_{id}.sav");
        public static string BackupSavePath => Path.Combine(RootDir, BackupFileName);

        /// <summary>受保护文件：主存档备份（阶段 B/F 的唯一回滚来源）永不清理。</summary>
        private static bool IsProtectedFile(string path)
        {
            var name = Path.GetFileName(path);
            return string.Equals(name, BackupFileName, StringComparison.OrdinalIgnoreCase);
        }

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
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 删除文件失败 {path}: {e.Message}");
            }
        }

        private static float ReadWorldTime()
        {
            try
            {
                var tod = TimeOfDayController.Instance ?? LevelManager.Instance?.TimeOfDayController;
                return tod != null ? tod.Time : 0f;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 读取世界时间失败: {e.Message}");
                return 0f;
            }
        }
    }
}
