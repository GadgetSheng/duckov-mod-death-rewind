using System;
using System.Collections.Generic;
using System.IO;
using DeathRewind.Core;
using ItemStatsSystem;
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
        private readonly EntitySampler _sampler;

        /// <summary>上次采集时各容器的内容指纹（用于跳过未变化容器的重复序列化）。</summary>
        private readonly Dictionary<string, int> _lastFingerprints = new Dictionary<string, int>();

        /// <summary>上次采集时各容器的载荷（内容未变化时直接复用）。</summary>
        private readonly Dictionary<string, byte[]> _lastPayloads = new Dictionary<string, byte[]>();

        public AnchorBuilder(Config.ModConfig config, LiveStateStore store, AnchorRing ring, EntitySampler sampler = null)
        {
            _config = config;
            _store = store;
            _ring = ring;
            _sampler = sampler;
        }

        /// <summary>锚点文件根目录：&lt;Application.persistentDataPath&gt;/ModRewind（mod 私有数据目录）。</summary>
        public static string RootDir => Path.Combine(Application.persistentDataPath, "ModRewind");

        public Anchor Build(int id, double absTimestamp, double gridTimestamp)
        {
            // 兼容入口：同步走完整个流程（构建 + 后台 IO 等待）。
            // 正常运行时 ModBehaviour 用的是 BeginBuild / ContinueCapture / StartIo / CompleteBuild 四段式。
            var ctx = BeginBuild(id, absTimestamp, gridTimestamp);
            while (!ContinueCapture(ctx)) { /* 同步推进：一次性把所有容器采完 */ }
            StartIo(ctx);
            ctx.IoTask.GetAwaiter().GetResult();
            return CompleteBuild(ctx);
        }

        /// <summary>
        /// 第一段（主线程）：采集玩家 / 敌人 / 门 / 世界时间的即时状态，并列出待采集的容器。
        /// 这里不做任何序列化与文件 IO，耗时很短。
        /// </summary>
        public AnchorBuildContext BeginBuild(int id, double absTimestamp, double gridTimestamp)
        {
            var ctx = new AnchorBuildContext { Id = id, AbsTimestamp = absTimestamp, GridTimestamp = gridTimestamp };
            var anchor = new Anchor
            {
                Id = id,
                AbsTimestamp = absTimestamp,
                GridTimestamp = gridTimestamp,
                SceneKey = LevelAccess.CurrentSceneKey,
                Player = _store.GetPlayer()?.Clone(),
                World = new WorldState { TimeOfDay = WorldTime() },
            };
            // 缩略图：抓一帧画面（失败不影响锚点本身）
            anchor.PreviewPng = ThumbnailCapture.Capture();

            // 玩家装备槽载荷：CharacterItem 的槽位（护甲/头盔/手持武器等）不在背包 Inventory 里，
            // 背包载荷覆盖不到，必须单独采集（只在锚点构建时做一次，逐 tick 采集太贵）。
            if (anchor.Player != null)
            {
                try { anchor.Player.Slots = InventoryPayload.CaptureSlots(LevelAccess.MainCharacter?.CharacterItem); }
                catch (Exception e) { Debug.LogWarning($"[DeathRewind] 玩家装备槽采集失败: {e.Message}"); }
            }

            foreach (var key in _store.EnemyKeys)
            {
                var st = _store.GetEnemy(key)?.Clone();
                if (st == null) continue;
                // 敌人物品载荷：死亡时游戏会把死者物品整体搬进战利品箱（CreateFromItem → Detach），
                // 直接复活会得到空手敌人，必须采集锚点时刻的背包与装备槽（只对活着的敌人采集）。
                if (st.Alive)
                {
                    try
                    {
                        var cm = _sampler?.GetEnemyHealth(key)?.TryGetCharacter();
                        var charItem = cm?.CharacterItem;
                        if (charItem != null)
                        {
                            st.ItemPayload = InventoryPayload.Capture(charItem.Inventory);
                            st.Slots = InventoryPayload.CaptureSlots(charItem);
                        }
                    }
                    catch (Exception e) { Debug.LogWarning($"[DeathRewind] 敌人 {key} 物品采集失败: {e.Message}"); }
                }
                anchor.Enemies[key] = st;
            }
            foreach (var key in _store.DoorKeys) anchor.Doors[key] = _store.GetDoor(key)?.Clone();
            ctx.Anchor = anchor;

            var map = LevelAccess.LootBoxInventories;
            if (map != null)
            {
                foreach (var kv in map) ctx.ContainerKeys.Add(new KeyValuePair<string, int>("loot:" + kv.Key, kv.Key));
            }
            return ctx;
        }

        /// <summary>
        /// 第二段（主线程，分帧推进）：每帧只序列化有限个容器，并在时间预算内停止。
        /// 返回 true 表示采集完成，可以进入后台 IO 阶段。
        /// </summary>
        public bool ContinueCapture(AnchorBuildContext ctx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int processed = 0;
            int batch = Math.Max(1, _config.AnchorCaptureBatchSize);
            double budget = Math.Max(0.5, _config.AnchorCaptureBudgetMs);
            var map = LevelAccess.LootBoxInventories;

            while (ctx.Cursor < ctx.ContainerKeys.Count && processed < batch && sw.Elapsed.TotalMilliseconds < budget)
            {
                var entry = ctx.ContainerKeys[ctx.Cursor++];
                processed++;
                Inventory inv = null;
                if (map != null) map.TryGetValue(entry.Value, out inv);
                if (inv == null) continue;

                int fingerprint = Fingerprint(inv);
                byte[] payload = null;
                if (_lastFingerprints.TryGetValue(entry.Key, out var prevFingerprint)
                    && prevFingerprint == fingerprint
                    && _lastPayloads.TryGetValue(entry.Key, out var prevPayload))
                {
                    // 容器内容自上次采集以来没有变化 → 直接复用上次的载荷，省掉一次全量序列化
                    payload = prevPayload;
                }
                else
                {
                    payload = InventoryPayload.Capture(inv);
                    _lastPayloads[entry.Key] = payload;
                    _lastFingerprints[entry.Key] = fingerprint;
                }

                ctx.Anchor.Containers[entry.Key] = new ContainerState
                {
                    Key = entry.Key,
                    Capacity = inv.Capacity,
                    LockedIndexes = inv.lockedIndexes != null ? inv.lockedIndexes.ToArray() : new int[0],
                    ItemPayload = payload,
                };
            }

            ctx.CaptureMs += sw.ElapsedMilliseconds;
            if (ctx.Cursor >= ctx.ContainerKeys.Count)
            {
                ctx.CaptureDone = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 第三段：把载荷写盘与主存档复制交给**后台线程**。
        /// 后台线程只做 System.IO 与字符串/字节操作，绝不触碰 Unity / 游戏 API。
        /// <para>
        /// 元系统快照语义：这里复制的是**游戏自己最后一次落盘**的主存档，而不是"当场调用
        /// SavesSystem.SaveFile() 强制落盘"的版本 —— 后者要在主线程写 ~1 MB 并生成备份，
        /// 实测是卡顿主因（单次 2.6 秒）。语义上的差别只是：锚点时刻与游戏上次落盘之间
        /// 产生的元系统变更（EXP / 任务）不会被回退到锚点时刻，而是保持在游戏上次落盘的水位；
        /// 这与游戏自己在崩溃后读档的结果一致。
        /// </para>
        /// </summary>
        public void StartIo(AnchorBuildContext ctx)
        {
            var anchor = ctx.Anchor;
            try { Directory.CreateDirectory(RootDir); }
            catch (Exception e) { ctx.IoError = "创建目录失败: " + e.Message; return; }

            anchor.ContainerPayloadPath = AnchorPayloadPath(ctx.Id);
            anchor.MainSaveCopyPath = AnchorSavePath(ctx.Id);

            // 主线程上把路径与内容都备好，后台线程只负责搬运字节
            string payloadPath = anchor.ContainerPayloadPath;
            string tmpPayloadPath = payloadPath + ".tmp";
            string saveCopyPath = anchor.MainSaveCopyPath;
            string mainSavePath = GetMainSavePath();

            var lines = new List<string>();
            foreach (var kv in anchor.Containers)
                if (kv.Value?.ItemPayload != null) lines.Add(kv.Key + "\t" + Convert.ToBase64String(kv.Value.ItemPayload));
            if (anchor.Player?.BagPayload != null) lines.Add(PlayerPayloadKey + "\t" + Convert.ToBase64String(anchor.Player.BagPayload));
            string payloadText = string.Join("\n", lines);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.IoTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    File.WriteAllText(tmpPayloadPath, payloadText, new System.Text.UTF8Encoding(false));
                    if (File.Exists(payloadPath)) File.Delete(payloadPath);
                    File.Move(tmpPayloadPath, payloadPath);
                    return TryCopyWithVerify(mainSavePath, saveCopyPath);
                }
                catch (Exception e)
                {
                    ctx.IoError = e.Message;
                    return false;
                }
                finally
                {
                    sw.Stop();
                    ctx.IoMs = sw.ElapsedMilliseconds;
                }
            });
        }

        /// <summary>后台 IO 是否已完成。</summary>
        public bool IsIoCompleted(AnchorBuildContext ctx) => ctx.IoTask != null && ctx.IoTask.IsCompleted;

        /// <summary>
        /// 第四段：等待后台 IO 结果。成功则返回可用的锚点；失败返回 null（该锚点不可用于回退，
        /// 阶段 A 会因主存档副本缺失而拒绝回退 —— 这是期望的降级）。
        /// </summary>
        public Anchor CompleteBuild(AnchorBuildContext ctx)
        {
            bool ok = false;
            try { ok = ctx.IoTask != null && ctx.IoTask.GetAwaiter().GetResult(); }
            catch (Exception e) { ctx.IoError = e.Message; }
            if (!ok)
            {
                Debug.LogWarning($"[DeathRewind] 锚点 {ctx.Id} 落盘失败（{ctx.IoError ?? "主存档副本校验未通过"}），该锚点不可用于回退");
                return null;
            }
            VerifyPlayerPayload(ctx.Anchor);
            Debug.Log($"[DeathRewind] 锚点 {ctx.Id} 构建完成: 主线程采集 {ctx.CaptureMs}ms（{ctx.ContainerKeys.Count} 个容器）/ 后台 IO {ctx.IoMs}ms / 合计 {ctx.CaptureMs + ctx.IoMs}ms");
            return ctx.Anchor;
        }

        /// <summary>关卡切换时清空"未变化容器复用"缓存（载荷属于具体关卡）。</summary>
        public void ResetCache()
        {
            _lastFingerprints.Clear();
            _lastPayloads.Clear();
        }

        /// <summary>容器内容指纹：条目数 + 各物品引用哈希（会话内有效，足以发现"被翻过/被塞过"）。</summary>
        private static int Fingerprint(Inventory inv)
        {
            int hash = 17;
            var content = inv?.Content;
            if (content == null) return hash;
            hash = hash * 31 + content.Count;
            for (int i = 0; i < content.Count; i++)
            {
                var item = content[i];
                hash = hash * 31 + (item == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item));
            }
            return hash;
        }

        /// <summary>后台线程用的复制：校验长度，不一致重试一次。</summary>
        private static bool TryCopyWithVerify(string src, string dest)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (!File.Exists(src)) return false;
                    long len = new FileInfo(src).Length;
                    File.Copy(src, dest, true);
                    if (new FileInfo(dest).Length == len) return true;
                }
                catch { /* 重试 */ }
            }
            return false;
        }

        /// <summary>当前世界时间（小时）。TimeOfDayController.Time 是实例属性，取不到时返回 0。</summary>
        private static float WorldTime()
        {
            try
            {
                var tod = TimeOfDayController.Instance ?? LevelManager.Instance?.TimeOfDayController;
                return tod != null ? tod.Time : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>玩家背包载荷的往返自检（容器不做逐个自检：太贵）。</summary>
        private static void VerifyPlayerPayload(Anchor anchor)
        {
            try
            {
                var main = LevelAccess.MainCharacter;
                if (main != null && !InventoryPayload.VerifyRoundTrip(main.CharacterItem?.Inventory))
                    Debug.LogWarning($"[DeathRewind] 锚点 {anchor?.Id} 玩家背包载荷往返自检失败");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 锚点 {anchor?.Id} 载荷自检失败: {e.Message}");
            }
        }

        /// <summary>
        /// 从锚点载荷文件读回容器/玩家背包载荷（自有格式：每行 key&lt;TAB&gt;base64）。
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
                foreach (var line in File.ReadAllLines(anchor.ContainerPayloadPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string key = line.Substring(0, tab);
                    string b64 = line.Substring(tab + 1);
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(b64); }
                    catch { continue; }
                    if (string.Equals(key, PlayerPayloadKey, System.StringComparison.Ordinal)) playerBag = bytes;
                    else containers[key] = bytes;
                }
                return containers.Count > 0 || playerBag != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 读取锚点载荷失败 {anchor.ContainerPayloadPath}: {e.Message}");
                return false;
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
            TryDelete(LegacyAnchorPayloadPath(anchor.Id));   // 清理旧格式遗留文件
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

        public static string AnchorPayloadPath(int id) => Path.Combine(RootDir, $"anchor_{id}.payload");
        public static string LegacyAnchorPayloadPath(int id) => Path.Combine(RootDir, $"anchor_{id}.es3");
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
