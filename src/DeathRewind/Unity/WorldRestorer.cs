using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DeathRewind.Core;
using Duckov.Buffs;
using Duckov.Utilities;
using ItemStatsSystem;
using Saves;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>恢复流程的阶段标识（日志字段与规格 §11.3 对齐）。</summary>
    public enum RestorePhase { A, B, C, D, E, E2, E3, E4, E5, E6, F }

    /// <summary>
    /// 分阶段恢复：阶段 A 校验与准备 → B 元系统存档 → C 容器背包 → D 敌人/门/时间/子弹 →
    /// E 玩家（E1..E6） → F 补偿。
    /// 每个阶段的异步操作都必须等待终态；超时只是"判定失败"的触发条件，不是跳过等待的许可。
    /// </summary>
    public sealed class WorldRestorer
    {
        private const double PhaseTimeoutSeconds = 30.0;
        /// <summary>回溯复活血量下限（占最大血量比例）。</summary>
        internal const float ReviveHealthFloorRatio = 0.5f;

        private readonly Config.ModConfig _config;
        private readonly AnchorRing _ring;
        private readonly LiveStateStore _store;
        private readonly MetaRestore _meta = new MetaRestore();

        private readonly List<string> _uncovered = new List<string>();

        private EntitySampler _sampler;
        private PreRewindSnapshot _pre;
        private DeathSnapshot _death;
        private DamageInfo _capturedDamage;
        private string _backupPath;

        private int _enemiesChanged;
        private int _doorsChanged;
        private int _containersChanged;
        private float _timeRestored;
        private string _worldState = "before";

        public WorldRestorer(Config.ModConfig config, AnchorRing ring, LiveStateStore store)
        {
            _config = config;
            _ring = ring;
            _store = store;
        }

        /// <summary>注入实体注册表（按稳定 ID 找回敌人/门对象）。</summary>
        public void SetSampler(EntitySampler sampler) => _sampler = sampler;

        /// <summary>记录拦截时刻的玩家死亡态与伤害信息（E6 反向复位与强制死亡兜底的权威来源）。</summary>
        public void SetDeathContext(DeathSnapshot death, DamageInfo captured)
        {
            _death = death;
            _capturedDamage = captured;
        }

        // ------------------------------------------------------------------ 编排

        public async Task<bool> RestoreAsync(Anchor anchor)
        {
            if (anchor == null)
            {
                Debug.LogError("[DeathRewind] restore phase=A result=failed: 锚点为空");
                return false;
            }

            _uncovered.Clear();
            _enemiesChanged = 0;
            _doorsChanged = 0;
            _containersChanged = 0;
            _timeRestored = 0f;
            _worldState = "before";

            if (!PhaseA(anchor)) return false;                       // A 失败：什么都没改，直接放弃回退
            bool full = IsFullScope();
            // position 模式：只回溯玩家位置（物品与敌人不回溯），跳过 B/C/D 三个世界级阶段
            if (full)
            {
                if (!PhaseB(anchor)) { await PhaseFAsync(anchor); return false; }
                if (!await PhaseCAsync(anchor)) { await PhaseFAsync(anchor); return false; }
                if (!await PhaseDAsync(anchor)) { await PhaseFAsync(anchor); return false; }
            }
            if (!await PhaseEAsync(anchor)) { await PhaseE6Async(); await PhaseFAsync(anchor); return false; }

            // 成功收尾：实时状态整体替换为锚点状态（规格 §6.7）
            if (full) _store.ReplaceFrom(anchor);
            _worldState = "anchor";
            LogPlayerState();
            LogWorldDiff();
            LogMeta();
            LogUncovered();
            DumpPostState();
            return true;
        }

        /// <summary>是否完整世界回滚（position 模式只回溯玩家位置）。</summary>
        private bool IsFullScope() => string.Equals(_config.RewindScope, "full", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>统一超时包装：超时只是"判定失败"的触发条件，返回 false 表示该阶段失败。</summary>
        private static async Task<bool> WithTimeout(Task<bool> work, double seconds, string phase)
        {
            var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds)));
            bool timedOut = finished != work;
            bool result = await work;   // 必须等待终态：超时也不跳过在途链（否则阶段 F 会与在途链并发）
            if (timedOut)
            {
                Debug.LogError($"[DeathRewind] restore phase={phase} 超时（>{seconds}s）");
                return false;
            }
            return result;
        }

        private bool ForceFail(string phase) => _config.DebugMode && _config.ForceFailPhase == phase;

        // ------------------------------------------------------------------ 阶段 A

        private bool PhaseA(Anchor anchor)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("A")) throw new InvalidOperationException("forced failure at A");

                bool full = IsFullScope();
                if (full)
                {
                    // 1) 先刷新存档，使"回退前状态" = 磁盘 + 内存双侧一致态
                    SavesSystem.CollectSaveData();
                    SavesSystem.SaveFile();

                    // 2) 备份主存档（路径留作字段，供阶段 B 失败与阶段 F 回滚）
                    _backupPath = _meta.BackupCurrent();
                    if (string.IsNullOrEmpty(_backupPath) || !File.Exists(_backupPath))
                        throw new IOException("主存档备份失败");
                }

                // 3) 采集回退前世界快照（position 模式不需要：世界不会被改动）
                if (full)
                {
                    _pre = CapturePreRewindSnapshot();
                    DumpPreState();
                }

                // 4) 校验锚点载荷可解析
                foreach (var kv in anchor.Containers)
                {
                    if (kv.Value?.ItemPayload == null) continue;
                    if (InventoryPayload.Parse(kv.Value.ItemPayload) == null)
                        throw new InvalidOperationException("锚点容器载荷无法解析: " + kv.Key);
                }
                if (anchor.Player?.BagPayload != null && InventoryPayload.Parse(anchor.Player.BagPayload) == null)
                    throw new InvalidOperationException("锚点玩家背包载荷无法解析");
                // 主存档副本只在完整回滚（full）模式下存在；position 模式锚点不回滚元系统，无副本文件
                if (full && (string.IsNullOrEmpty(anchor.MainSaveCopyPath) || !File.Exists(anchor.MainSaveCopyPath)))
                    throw new FileNotFoundException("锚点主存档副本缺失", anchor.MainSaveCopyPath ?? "<null>");

                ok = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (!ok)
            {
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=A result=failed: {error}");
                else Log(RestorePhase.A, "failed");
                return false;
            }
            Log(RestorePhase.A, "ok");
            return true;
        }

        /// <summary>回退前世界快照：世界时间、玩家位姿与背包、每个容器的 InventoryData 副本、每扇门的 closed、每个敌人的实时态。</summary>
        private PreRewindSnapshot CapturePreRewindSnapshot()
        {
            var pre = new PreRewindSnapshot();
            pre.World = new WorldState { TimeOfDay = ReadWorldTime() };

            var main = LevelManager.Instance?.MainCharacter;
            if (main != null)
            {
                pre.PlayerPosition = ToArray3(main.transform.position);
                pre.PlayerRotation = ToArray4(main.transform.rotation);
                pre.PlayerBagPayload = InventoryPayload.Capture(main.CharacterItem?.Inventory);
            }

            var map = LevelAccess.LootBoxInventories;
            if (map != null)
            {
                foreach (var kv in map)
                {
                    if (kv.Value == null) continue;
                    string key = ContainerKey(kv.Key);
                    pre.Containers[key] = new ContainerState
                    {
                        Key = key,
                        Capacity = kv.Value.Capacity,
                        LockedIndexes = kv.Value.lockedIndexes != null ? kv.Value.lockedIndexes.ToArray() : Array.Empty<int>(),
                        ItemPayload = InventoryPayload.Capture(kv.Value),
                    };
                }
            }

            foreach (var key in _store.DoorKeys)
            {
                var door = GetDoor(key);
                if (door != null)
                {
                    pre.Doors[key] = new DoorState { Key = key, Closed = !door.IsOpen };
                    continue;
                }
                var stored = _store.GetDoor(key);
                if (stored != null) pre.Doors[key] = stored.Clone();
            }

            foreach (var key in _store.EnemyKeys)
            {
                var h = GetEnemyHealth(key);
                if (h != null)
                {
                    pre.Enemies[key] = new EnemyState
                    {
                        Position = ToArray3(h.transform.position),
                        Rotation = ToArray4(h.transform.rotation),
                        Health = h.CurrentHealth,
                        Alive = !h.IsDead && h.gameObject.activeSelf,
                        AiTargetId = ResolveAiTargetId(h),
                    };
                    continue;
                }
                var stored = _store.GetEnemy(key);
                if (stored != null) pre.Enemies[key] = stored.Clone();
            }

            return pre;
        }

        // ------------------------------------------------------------------ 阶段 B

        private bool PhaseB(Anchor anchor)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("B")) throw new InvalidOperationException("forced failure at B");
                ok = _meta.OverwriteFrom(anchor.MainSaveCopyPath);
                if (!ok) error = "主存档覆盖失败";
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (!ok)
            {
                // 失败：先完成磁盘回滚（再判定失败）
                RollbackDisk();
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=B result=failed: {error}");
                else Log(RestorePhase.B, "failed");
                return false;
            }
            Log(RestorePhase.B, "ok");
            return true;
        }

        /// <summary>磁盘回滚：用 A 阶段的备份覆盖回主存档并触发重载。</summary>
        private bool RollbackDisk()
        {
            if (string.IsNullOrEmpty(_backupPath) || !File.Exists(_backupPath))
            {
                Debug.LogError("[DeathRewind] 磁盘回滚失败：备份不存在");
                return false;
            }
            if (!_meta.OverwriteFrom(_backupPath))
            {
                Debug.LogError("[DeathRewind] 磁盘回滚失败：覆盖主存档失败");
                return false;
            }
            // OverwriteFrom 内部已调用 SavesSystem.SetFile(CurrentSlot)（缓存指向主存档路径并触发 OnSetFile）
            try { SavesSystem.SetFile(SavesSystem.CurrentSlot); }
            catch (Exception e) { ModLog.LogWarning($"[DeathRewind] 磁盘回滚后重载存档失败: {e.Message}"); }
            return true;
        }

        // ------------------------------------------------------------------ 阶段 C

        private async Task<bool> PhaseCAsync(Anchor anchor)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("C")) throw new InvalidOperationException("forced failure at C");

                var map = LevelAccess.LootBoxInventories;
                if (map != null)
                {
                    // 0) 锚点之后新出现的战利品箱（锚点后死亡角色的掉落）→ 直接销毁，
                    //    否则"已死敌人的掉落"会残留在回溯后的世界里。被复活敌人的物品
                    //    改由锚点载荷重建（阶段 D），因此销毁箱子不会造成物品丢失。
                    DestroyLootBoxesNotInAnchor(anchor, map);

                    // 载荷来源：优先内存中的锚点载荷，缺失时回落到锚点载荷文件（只读一次）
                    AnchorBuilder.TryLoadPayloads(anchor, out var filePayloads, out _);

                    // 1) 整体校验：所有容器载荷必须先能解析
                    foreach (var kv in map)
                    {
                        if (kv.Value == null) continue;
                        string key = ContainerKey(kv.Key);
                        var payload = ResolveContainerPayload(anchor, key, filePayloads);
                        if (payload == null) continue;
                        if (InventoryPayload.Parse(payload) == null)
                            throw new InvalidOperationException("锚点容器载荷无法解析: " + key);
                    }

                    // 2) 逐个原地还原（逐个等待完成，超时后仍等待终态）
                    foreach (var kv in map)
                    {
                        if (kv.Value == null) continue;
                        string key = ContainerKey(kv.Key);
                        var payload = ResolveContainerPayload(anchor, key, filePayloads);
                        if (payload == null) continue;
                        bool applied = await WithTimeout(InventoryPayload.ApplyAsync(payload, kv.Value), PhaseTimeoutSeconds, "C");
                        if (!applied)
                        {
                            Log(RestorePhase.C, "failed");
                            return false;
                        }
                        _containersChanged++;
                    }
                }
                ok = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (!ok)
            {
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=C result=failed: {error}");
                else Log(RestorePhase.C, "failed");
                return false;
            }
            Log(RestorePhase.C, "ok");
            return true;
        }

        /// <summary>容器载荷来源：优先内存中的锚点载荷，缺失时回落到锚点载荷文件。</summary>
        private static byte[] ResolveContainerPayload(Anchor anchor, string key, Dictionary<string, byte[]> filePayloads)
        {
            if (anchor.Containers.TryGetValue(key, out var state) && state?.ItemPayload != null)
                return state.ItemPayload;
            if (filePayloads != null && filePayloads.TryGetValue(key, out var p)) return p;
            return null;
        }

        /// <summary>
        /// 销毁锚点时刻不存在的战利品箱：角色死亡会把死者物品整体搬进战利品箱（CreateFromItem），
        /// 锚点后击杀产生的箱子在回溯后属于"多余世界状态"。被复活敌人的物品改由锚点载荷重建，
        /// 因此销毁箱子不会造成物品丢失。
        /// </summary>
        private static void DestroyLootBoxesNotInAnchor(Anchor anchor, Dictionary<int, Inventory> map)
        {
            MethodInfo getKey = null;
            try
            {
                getKey = typeof(InteractableLootbox).GetMethod("GetKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch { /* 反射失败：跳过清理，避免误删 */ }
            if (getKey == null)
            {
                ModLog.LogWarning("[DeathRewind] InteractableLootbox.GetKey 反射失败，跳过多余战利品箱清理");
                return;
            }

            int removed = 0;
            var boxes = UnityEngine.Object.FindObjectsByType<InteractableLootbox>(FindObjectsSortMode.None);
            foreach (var box in boxes)
            {
                if (box == null) continue;
                int key;
                try { key = (int)getKey.Invoke(box, null); }
                catch { continue; }
                if (anchor.Containers.ContainsKey(ContainerKey(key))) continue;
                // 先从注册表移除，避免后续遍历命中已销毁对象；再销毁本体（含箱内物品）
                map.Remove(key);
                UnityEngine.Object.Destroy(box.gameObject);
                removed++;
            }
            if (removed > 0) ModLog.Log($"[DeathRewind] restore phase=C 移除锚点后新增战利品箱 x{removed}");
        }

        // ------------------------------------------------------------------ 阶段 D

        private async Task<bool> PhaseDAsync(Anchor anchor)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("D")) throw new InvalidOperationException("forced failure at D");
                await ApplyEnemyDiffAsync(anchor);
                ApplyDoorDiff(anchor);
                ApplyWorldTime(anchor);
                DestroyProjectiles();
                ok = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (!ok)
            {
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=D result=failed: {error}");
                else Log(RestorePhase.D, "failed");
                return false;
            }
            Log(RestorePhase.D, "ok");
            return true;
        }

        /// <summary>
        /// 正向差量表（锚点 A → 回退前实时 B）。
        /// 复活的敌人还需按锚点重建物品与装备槽——死亡时物品已被整体搬进战利品箱。
        /// </summary>
        private async Task ApplyEnemyDiffAsync(Anchor anchor)
        {
            var ids = new HashSet<string>();
            foreach (var kv in anchor.Enemies) ids.Add(kv.Key);
            foreach (var key in _store.EnemyKeys) ids.Add(key);

            var itemJobs = new List<KeyValuePair<string, EnemyState>>();
            foreach (var id in ids)
            {
                anchor.Enemies.TryGetValue(id, out var a);
                var h = GetEnemyHealth(id);
                bool missing = h == null;                                       // 对象已被游戏销毁
                bool dead = !missing && (h.IsDead || !h.gameObject.activeSelf);  // 已死或已停用
                bool aAlive = a != null && a.Alive;

                if (aAlive)
                {
                    if (missing)
                    {
                        // A 活 / B 无：对象已被游戏销毁，没有预制体来源可重建 → 不复活，记入未覆盖清单
                        AddUncovered($"enemy:{id}:destroyed");
                        continue;
                    }
                    if (dead)
                    {
                        // 复活：AI 复位为 null（不写回锚点的 AI 目标）
                        ReviveEnemy(h, a, id);
                        // 死亡时物品已被搬进战利品箱，需按锚点载荷重建背包与装备槽
                        if ((a.ItemPayload != null && a.ItemPayload.Length > 0) || (a.Slots != null && a.Slots.Length > 0))
                            itemJobs.Add(new KeyValuePair<string, EnemyState>(id, a));
                    }
                    else
                    {
                        WriteEnemyState(h, a, id);
                    }
                    _enemiesChanged++;
                    continue;
                }

                if (a == null)
                {
                    // 锚点时刻尚不存在的敌人（锚点后刷出）：无论死活一律移除，
                    // 否则"锚点后死亡的敌人尸体"会残留在回溯后的世界里。
                    if (missing) continue;
                    h.gameObject.SetActive(false);
                    _sampler?.MarkDeferredDestroy(id);
                    _enemiesChanged++;
                    continue;
                }

                // A 死 / B 活
                if (missing || dead) continue;   // A 死 B 死：无操作（保留尸体）
                h.gameObject.SetActive(false);     // 移除：只停用，销毁推迟到提交成功后
                _sampler?.MarkDeferredDestroy(id);
                _enemiesChanged++;
            }

            // 复活敌人的物品与装备槽重建（非致命：失败记入未覆盖清单）
            foreach (var job in itemJobs)
            {
                try
                {
                    await RestoreEnemyItemsAsync(job.Key, job.Value);
                }
                catch (Exception e)
                {
                    AddUncovered($"enemy:{job.Key}:items");
                    ModLog.LogWarning($"[DeathRewind] 敌人 {job.Key} 物品还原失败: {e.Message}");
                }
            }
        }

        /// <summary>写回敌人的位置/旋转/血量与 AI 状态（索敌目标与察觉标志）。</summary>
        private void WriteEnemyState(Health h, EnemyState a, string id = null)
        {
            if (h == null || a == null) return;
            WriteEnemyTransform(h, a);
            ApplyAiState(h, a, id);
        }

        /// <summary>只写回位姿与血量。</summary>
        private static void WriteEnemyTransform(Health h, EnemyState a)
        {
            if (h == null || a == null) return;
            h.transform.position = ToVector3(a.Position);
            h.transform.rotation = ToQuaternion(a.Rotation);
            try { h.SetHealth(a.Health); } catch { /* 血量写入失败不阻断差量表 */ }
        }

        /// <summary>
        /// AI 状态：写回索敌目标与"是否已察觉"。
        /// 敌人的 AICharacterController 可能不在 Health 所在的 GameObject 上，必须经注册表找回。
        /// 不写回察觉标志会让敌人停在"困惑（问号）"状态。
        /// </summary>
        private void ApplyAiState(Health h, EnemyState a, string id)
        {
            if (h == null || a == null) return;
            var ai = id != null ? _sampler?.GetEnemyAi(id) : null;
            if (ai == null) ai = EntitySampler.FindAiController(h);
            if (ai == null) return;

            DamageReceiver targetReceiver = null;
            if (!string.IsNullOrEmpty(a.AiTargetId))
            {
                targetReceiver = a.AiTargetId == "player"
                    ? _sampler?.GetPlayerReceiver()
                    : _sampler?.GetEnemyReceiver(a.AiTargetId);
            }
            ai.searchedEnemy = targetReceiver;
            ai.SetTarget(targetReceiver != null ? targetReceiver.transform : null);
            ai.noticed = a.Noticed;
        }

        /// <summary>复活：启用对象、清 isDead、写回血量与位姿，并恢复锚点时刻的索敌状态。</summary>
        private void ReviveEnemy(Health h, EnemyState a, string id = null)
        {
            if (h == null) return;
            h.gameObject.SetActive(true);
            // Health 挂在子物体上：角色根物体也要一起激活，否则敌人"活着但不可见/不可交互"
            try
            {
                var character = h.TryGetCharacter();
                if (character != null) character.gameObject.SetActive(true);
            }
            catch { /* 忽略 */ }
            SetBoolField(h, "isDead", false);
            // 死亡流程会把 hasHandledDeath 置 true（OnDead 只执行一次），
            // 复活时必须复位，否则敌人无法重新进入正常战斗状态（不能攻击）。
            try
            {
                var cm = h.TryGetCharacter();
                if (cm != null) SetBoolField(cm, "hasHandledDeath", false);
            }
            catch { /* 忽略 */ }
            try { h.SetHealth(a != null ? a.Health : h.CurrentHealth); } catch { /* 忽略 */ }
            if (a != null)
            {
                h.transform.position = ToVector3(a.Position);
                h.transform.rotation = ToQuaternion(a.Rotation);
            }
            var ai = id != null ? _sampler?.GetEnemyAi(id) : null;
            if (ai == null) ai = EntitySampler.FindAiController(h);
            if (ai == null) return;
            ai.StopMove();
            // 索敌目标与察觉标志按锚点恢复：置 null 会让敌人进入"困惑（问号）"状态
            ApplyAiState(h, a, id);
            if (a == null || string.IsNullOrEmpty(a.AiTargetId))
            {
                ai.SetTarget(null);
                ai.noticed = false;
            }
        }

        /// <summary>按锚点载荷重建复活敌人的背包与装备槽（死亡时物品已被整体搬进战利品箱）。</summary>
        private async Task<bool> RestoreEnemyItemsAsync(string id, EnemyState a)
        {
            if (a == null || string.IsNullOrEmpty(id)) return false;
            var h = GetEnemyHealth(id);
            if (h == null) return false;
            var charItem = h.TryGetCharacter()?.CharacterItem;
            if (charItem == null) return false;

            bool ok = true;
            if (a.ItemPayload != null && a.ItemPayload.Length > 0 && charItem.Inventory != null)
                ok = await WithTimeout(InventoryPayload.ApplyAsync(a.ItemPayload, charItem.Inventory), PhaseTimeoutSeconds, "D") && ok;
            if (a.Slots != null && a.Slots.Length > 0)
                ok = await WithTimeout(InventoryPayload.ApplySlotsAsync(a.Slots, charItem), PhaseTimeoutSeconds, "D") && ok;
            return ok;
        }

        /// <summary>门：按锚点状态强制开合（不触发事件）。</summary>
        private void ApplyDoorDiff(Anchor anchor)
        {
            foreach (var kv in anchor.Doors)
            {
                var door = GetDoor(kv.Key);
                if (door == null)
                {
                    AddUncovered($"door:{kv.Key}:missing");
                    continue;
                }
                bool closed = kv.Value != null && kv.Value.Closed;
                door.ForceSetClosed(closed, false);
                _doorsChanged++;
            }
        }

        /// <summary>世界时间：写回 TimeOfDayController 的私有字段 time。</summary>
        private void ApplyWorldTime(Anchor anchor)
        {
            float t = anchor.World != null ? anchor.World.TimeOfDay : 0f;
            var tod = GetTimeOfDayController();
            if (tod == null)
            {
                AddUncovered("worldtime:controller-missing");
                return;
            }
            SetFloatField(tod, "time", t);
            _timeRestored = t;
        }

        /// <summary>子弹：销毁场上所有 Projectile（含玩家自己的）。</summary>
        private static void DestroyProjectiles()
        {
            var projectiles = UnityEngine.Object.FindObjectsByType<Projectile>(FindObjectsSortMode.None);
            foreach (var p in projectiles)
            {
                if (p == null) continue;
                p.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(p.gameObject);
            }
        }

        // ------------------------------------------------------------------ 阶段 E

        private async Task<bool> PhaseEAsync(Anchor anchor)
        {
            // E1 复核 DeathSnapshot
            if (_death == null)
            {
                Debug.LogError("[DeathRewind] 缺少 DeathSnapshot（E1 复核失败）");
                Log(RestorePhase.E5, "failed");
                Log(RestorePhase.E, "failed");
                return false;
            }
            if (ForceFail("E"))
            {
                Debug.LogError("[DeathRewind] 注入的强制失败（E）");
                Log(RestorePhase.E5, "failed");
                Log(RestorePhase.E, "failed");
                return false;
            }

            if (!await PhaseE2Async(anchor)) { Log(RestorePhase.E5, "failed"); Log(RestorePhase.E, "failed"); return false; }
            if (!PhaseE3()) { Log(RestorePhase.E5, "failed"); Log(RestorePhase.E, "failed"); return false; }
            if (!PhaseE4()) { Log(RestorePhase.E5, "failed"); Log(RestorePhase.E, "failed"); return false; }

            Log(RestorePhase.E5, "ok");
            Log(RestorePhase.E, "ok");
            return true;
        }

        /// <summary>E2：传送 + SetHealth + 背包原地还原（失败用 A 阶段副本回灌）。</summary>
        private async Task<bool> PhaseE2Async(Anchor anchor)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("E2")) throw new InvalidOperationException("forced failure at E2");
                var main = LevelManager.Instance?.MainCharacter;
                if (main == null) throw new InvalidOperationException("玩家对象缺失");

                if (anchor.Player != null)
                {
                    main.transform.position = ToVector3(anchor.Player.Position);
                    main.transform.rotation = ToQuaternion(anchor.Player.Rotation);
                    if (main.Health != null)
                    {
                        // 血量下限保护（参考 SekiroMod：复活血量 = 最大血量×50%）：
                        // 残血时刻的锚点回溯后等于立刻再死，回溯失去意义。
                        // 锚点血量高于下限时保留锚点原值（位置/预览图仍精确对应锚点时刻）。
                        float floored = Math.Max(anchor.Player.Health,
                            main.Health.MaxHealth * ReviveHealthFloorRatio);
                        main.Health.SetHealth(floored);
                    }
                }

                // position 模式：已获得的物品不回溯（背包与装备槽都保持现状）
                if (IsFullScope())
                {
                var bag = main.CharacterItem?.Inventory;
                byte[] payload = anchor.Player?.BagPayload;
                if (bag != null && payload != null)
                {
                    bool applied = await WithTimeout(InventoryPayload.ApplyAsync(payload, bag), PhaseTimeoutSeconds, "E2");
                    if (!applied) throw new InvalidOperationException("背包原地还原失败");
                }

                // 装备槽（护甲/头盔/手持武器及挂件）不在背包里；死亡流程会把它们 Detach 进掉落物，
                // 必须按锚点载荷重建，否则回溯后瞄准镜等附件丢失。
                // 注意：锚点后才加装的附件会随回溯消失（回溯语义：状态回到锚点时刻）。
                var slots = anchor.Player?.Slots;
                if (slots != null && slots.Length > 0 && main.CharacterItem != null)
                {
                    bool slotsOk = await WithTimeout(InventoryPayload.ApplySlotsAsync(slots, main.CharacterItem), PhaseTimeoutSeconds, "E2");
                    if (!slotsOk) ModLog.LogWarning("[DeathRewind] 玩家装备槽还原不完整（非致命）");
                }
                }   // end if (IsFullScope())

                ok = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (!ok)
            {
                // 背包可能已被清空/半还原：先回灌 A 阶段副本，再判失败
                await RevertPlayerBag();
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=E2 result=failed: {error}");
                else Log(RestorePhase.E2, "failed");
                return false;
            }
            Log(RestorePhase.E2, "ok");
            return true;
        }

        /// <summary>用 A 阶段的背包副本回灌玩家背包（原地还原）。</summary>
        private async Task<bool> RevertPlayerBag()
        {
            if (_pre?.PlayerBagPayload == null) return false;
            try
            {
                var main = LevelManager.Instance?.MainCharacter;
                var bag = main?.CharacterItem?.Inventory;
                if (bag == null) return false;
                return await WithTimeout(InventoryPayload.ApplyAsync(_pre.PlayerBagPayload, bag), PhaseTimeoutSeconds, "E2");
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 回灌背包副本失败: {e.Message}");
                return false;
            }
        }

        /// <summary>E3：复活标志复位 + RaidInfo 修正。</summary>
        private bool PhaseE3()
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("E3")) throw new InvalidOperationException("forced failure at E3");
                var main = LevelManager.Instance?.MainCharacter;
                if (main == null) throw new InvalidOperationException("玩家对象缺失");

                SetBoolField(main, "hasHandledDeath", false);
                if (main.Health != null)
                {
                    SetBoolField(main.Health, "isDead", false);
                    main.Health.SetInvincible(false);
                }
                main.gameObject.SetActive(true);
                var lm = LevelManager.Instance;
                if (lm != null) SetBoolField(lm, "dieTask", false);

                // RaidInfo：dead 仍为 true 时构造修正副本并写回存档
                var raid = RaidUtilities.CurrentRaid;
                if (raid.dead)
                {
                    var fixedRaid = raid;
                    fixedRaid.dead = false;
                    fixedRaid.ended = false;
                    SavesSystem.Save(RaidInfoSaveKey(), fixedRaid);
                    TrySetCurrentRaid(fixedRaid);
                }
                ok = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (ok) RestorePlayerPresentation();
            if (!ok)
            {
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=E3 result=failed: {error}");
                else Log(RestorePhase.E3, "failed");
                return false;
            }
            Log(RestorePhase.E3, "ok");
            return true;
        }

        /// <summary>
        /// 复活后的"表现层"恢复：
        /// ① Health / DamageReceiver 可能挂在子物体上，死亡时被 SetActive(false) → 需要一并激活；
        /// ② 准星：原生 <c>AimMarker.Update</c> 只在玩家死亡时把 rootCanvasGroup.alpha 设为 0，
        ///    恢复时**从不设回 1**（只有 Start() 会设）→ 复活后准星一直消失。
        /// </summary>
        private static void RestorePlayerPresentation()
        {
            var main = LevelManager.Instance?.MainCharacter;
            if (main == null) return;
            try
            {
                if (main.Health != null) main.Health.gameObject.SetActive(true);
                // 死亡流程 DamageReceiver.OnDead 会把接收器整个 GameObject SetActive(false)；
                // 它是失活的子物体，GetComponentInChildren 默认找不到 → 必须优先用公开字段直接引用
                var receiver = main.mainDamageReceiver
                    ?? main.GetComponent<DamageReceiver>()
                    ?? main.GetComponentInChildren<DamageReceiver>(true);
                if (receiver != null) receiver.gameObject.SetActive(true);
                ResetPlayerCombatState(main);
                main.gameObject.SetActive(true);
                ClearEnemyAggro();

                var aim = UnityEngine.Object.FindFirstObjectByType<AimMarker>();
                if (aim != null && aim.rootCanvasGroup != null) aim.rootCanvasGroup.alpha = 1f;

                var h = main.Health;
                ModLog.Log($"[DeathRewind] revive sanity: hp={h?.CurrentHealth} isDead={h?.IsDead} invincible={h?.Invincible} " +
                          $"playerActive={main.gameObject.activeSelf} healthActive={h?.gameObject.activeSelf} " +
                          $"receiverActive={(receiver != null && receiver.gameObject.activeSelf)} " +
                          $"crosshair={(aim != null && aim.rootCanvasGroup != null ? aim.rootCanvasGroup.alpha : -1f)}");
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 复活表现层恢复失败: {e.Message}");
            }
        }

        /// <summary>
        /// 战斗状态深度复位（参考创意工坊 SekiroMod「只狼mod」已验证的复活实践）：
        /// ① hidden/Show：CharacterMainControl.hidden=true 时 AI 视野直接看不见玩家；
        /// ② 队伍：AI 用 Team 校验索敌目标，死亡流程可能篡改 → 强制回 player；
        /// ③ 碰撞体重注册：接收器 GO 复活后 Unity 物理可能仍视为旧体 → 全部子 Collider
        ///    enable 翻转 + isTrigger=false + layer=DamageReceiver（DuckovHider 在我们的
        ///    拦截流里不会触发，无需处理）；
        /// ④ 刚体清速：防止复活瞬间带着死亡残留速度漂移。
        /// </summary>
        private static void ResetPlayerCombatState(CharacterMainControl main)
        {
            try
            {
                // ① AI 视野与显隐
                typeof(CharacterMainControl).GetField("hidden", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(main, false);
                try { main.Show(); }
                catch { /* Show 在部分场景可能不可用，失败不影响其余复位 */ }

                // ② 队伍（Health.team 是 public 字段）
                try { main.SetTeam(Teams.player); }
                catch { /* SetTeam 不可用则依赖下面直接赋值 */ }
                var health = main.Health;
                if (health != null) health.team = Teams.player;

                // ③ 接收器深度复位：组件启用翻转 + 调用原生 Reset（若存在）。
                //    注意：DamageReceiver 自身 GameObject 的 layer **绝不能改**——
                //    若 receiver 挂在 CharacterMainControl 本体或与其 Collider 共享 GO，
                //    改 layer 会让 CountDownArea / DoorTrigger / 区域触发器的 layerMask 过滤失效，
                //    表现为复活后进撤离点不计时、区域门不开等。enable 翻转已足够让 Unity 重建接触对。
                var receiver = main.mainDamageReceiver;
                if (receiver != null)
                {
                    int before = receiver.gameObject.layer;
                    receiver.enabled = false;
                    receiver.enabled = true;
                    typeof(DamageReceiver).GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.Invoke(receiver, null);
                    ModLog.Log($"[DeathRewind] DamageReceiver 复位: GO={receiver.gameObject.name} layer={before}(未改)");
                }

                // ③' 全部子碰撞体重注册（CharacterController 除外）：只做 enabled 翻转让 Unity 重建接触对。
                //     绝不能改 layer / isTrigger：玩家身体碰撞体原本在 "Character" 层，
                //     CountDownArea（撤离点）、DoorTrigger、区域触发器都按该层判定，
                //     改成 DamageReceiver 后触发器不再响应 → 复活后进撤离点无效的根因。
                int colliderCount = 0;
                foreach (var col in main.GetComponentsInChildren<Collider>(true))
                {
                    if (col is CharacterController) continue;
                    col.enabled = false;
                    col.enabled = true;
                    colliderCount++;
                }
                ModLog.Log($"[DeathRewind] 碰撞体复位: {colliderCount} 个（保留原 layer/isTrigger）");

                // ④ 刚体清速
                foreach (var rb in main.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb.isKinematic) continue;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                try { main.SetForceMoveVelocity(Vector3.zero); }
                catch { /* movementControl 不可用则跳过 */ }

                // ⑤ 状态清除：position 轻量模式不回溯 buff 层，
                //     死亡瞬间的流血/中毒/疼痛等会被原样带进复活（复活即被继续扣血），
                //     这里把死亡时残留的 buff 全部移除，只保留游戏注入的 BaseBuff。
                ClearStatusBuffs(main);

                Physics.SyncTransforms();
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 战斗状态深度复位失败: {e.Message}");
            }
        }

        /// <summary>
        /// 复活后清除死亡时残留的状态 buff（流血/中毒/疼痛/骨折/负重等）。
        /// position 轻量回溯不覆盖 buff 层，这些状态若留着会让玩家复活后继续掉血。
        /// BaseBuff（非突袭地图由游戏注入的基底 buff）保留，避免影响基础属性。
        /// </summary>
        private static void ClearStatusBuffs(CharacterMainControl main)
        {
            try
            {
                var manager = main.GetBuffManager();
                if (manager == null) return;

                int baseBuffId = -1;
                try
                {
                    var baseBuff = GameplayDataSettings.Buffs?.BaseBuff;
                    if (baseBuff != null) baseBuffId = baseBuff.ID;
                }
                catch { /* 配置缺失时退化为全部清除 */ }

                var snapshot = new List<Buff>(manager.Buffs);
                int cleared = 0;
                foreach (var buff in snapshot)
                {
                    if (buff == null || buff.ID == baseBuffId) continue;
                    try
                    {
                        manager.RemoveBuff(buff, false);
                        cleared++;
                    }
                    catch (Exception e)
                    {
                        ModLog.LogWarning($"[DeathRewind] 清除状态 buff={buff.ID} 失败: {e.Message}");
                    }
                }
                ModLog.Log($"[DeathRewind] 状态清除: {cleared} 个 buff（保留 BaseBuff）");
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 状态清除失败: {e.Message}");
            }
        }

        /// <summary>
        /// 复活后清除敌人对玩家的仇恨。
        /// position 轻量回溯不覆盖 AI 层，死亡瞬间已锁定玩家的敌人会继续追打复活后的玩家。
        /// 复位原生仇恨字段（searchedEnemy / aimTarget / noticed / lastDamageInfo），
        /// 战斗行为树下一帧失去目标后回到巡逻；forceTracePlayerDistance 属于近身强制索敌，
        /// 不复位——复活即贴脸时本来就该被发现。
        /// </summary>
        private static void ClearEnemyAggro()
        {
            try
            {
                var ais = UnityEngine.Object.FindObjectsByType<AICharacterController>(FindObjectsSortMode.None);
                int cleared = 0;
                foreach (var ai in ais)
                {
                    if (ai == null) continue;
                    var self = ai.CharacterMainControl;
                    if (self == null) continue;
                    try
                    {
                        if (!Team.IsEnemy(self.Team, Teams.player)) continue;   // 玩家/宠物/友军跳过
                    }
                    catch { continue; }

                    bool hadAggro = ai.searchedEnemy != null || ai.aimTarget != null || ai.noticed;
                    ai.searchedEnemy = null;
                    ai.aimTarget = null;
                    ai.noticed = false;
                    ai.foundDangerObject = null;
                    ai.lastDamageInfo = default;
                    ai.hurtTimeMarker = -999f;
                    if (hadAggro) cleared++;
                }
                ModLog.Log($"[DeathRewind] 敌人仇恨清除: {cleared}/{ais.Length} 个");
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 敌人仇恨清除失败: {e.Message}");
            }
        }

        /// <summary>E4：校验玩家确实活着。</summary>
        private bool PhaseE4()
        {
            var main = LevelManager.Instance?.MainCharacter;
            bool alive = main != null && main.Health != null && main.Health.CurrentHealth > 0 && !main.Health.IsDead;
            if (!alive)
            {
                Debug.LogError("[DeathRewind] E4 校验失败：玩家未复活");
                Log(RestorePhase.E4, "failed");
                return false;
            }
            Log(RestorePhase.E4, "ok");
            return true;
        }

        /// <summary>E6：以 DeathSnapshot 为唯一权威来源，把 E2/E3 改动的字段逐项改回死亡态。</summary>
        private async Task<bool> PhaseE6Async()
        {
            bool ok = false;
            string error = null;
            try
            {
                if (_death == null) throw new InvalidOperationException("缺少 DeathSnapshot");
                var main = LevelManager.Instance?.MainCharacter;
                if (main == null) throw new InvalidOperationException("玩家对象缺失");

                if (main.Health != null)
                {
                    main.Health.SetHealth(_death.Health);
                    SetBoolField(main.Health, "isDead", _death.IsDead);
                    main.Health.SetInvincible(_death.Invincible);
                }
                SetBoolField(main, "hasHandledDeath", _death.HandledDeath);
                main.gameObject.SetActive(_death.ActiveSelf);
                main.transform.position = ToVector3(_death.Position);
                main.transform.rotation = ToQuaternion(_death.Rotation);

                var bag = main.CharacterItem?.Inventory;
                if (bag != null && _death.BagPayload != null)
                    await WithTimeout(InventoryPayload.ApplyAsync(_death.BagPayload, bag), PhaseTimeoutSeconds, "E6");

                var raid = RaidUtilities.CurrentRaid;
                if (raid.dead != _death.RaidDead)
                {
                    var restored = raid;
                    restored.dead = _death.RaidDead;
                    SavesSystem.Save(RaidInfoSaveKey(), restored);
                    TrySetCurrentRaid(restored);
                }
                var lm = LevelManager.Instance;
                // 注意：dieTask 必须复位为 false，而不是 _death.IsDead。
                // 拦截时游戏原方法体被跳过，dieTask 从未被置位（此时为 false）；
                // 若在此置为 true，随后重放的 OnMainCharacterDie 会因 `if (!dieTask)` 不成立而永不执行，
                // 死亡流程（CharacterDieTask → ClosureView → 失败场景）将卡死。
                if (lm != null) SetBoolField(lm, "dieTask", false);

                ok = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (!ok)
            {
                Debug.LogError($"[DeathRewind] E6 反向复位失败: {error}");
                Log(RestorePhase.E6, "failed");
                ForceKillPlayer();
                return false;
            }
            Log(RestorePhase.E6, "ok");
            return true;
        }

        /// <summary>最后兜底：用捕获的原始 DamageInfo 强制玩家进入死亡态。</summary>
        private void ForceKillPlayer()
        {
            try
            {
                var main = LevelManager.Instance?.MainCharacter;
                if (main?.Health == null) return;
                DamageInfo dmg = _capturedDamage;
                if (dmg.damageValue <= 0f)
                {
                    dmg = new DamageInfo
                    {
                        damageValue = 1e6f,
                        finalDamage = 1e6f,
                        toDamageReceiver = main.GetComponent<DamageReceiver>(),
                        damagePoint = main.transform.position,
                    };
                }
                main.Health.Hurt(dmg);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 强制死亡兜底失败: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ 阶段 F

        private async Task<bool> PhaseFAsync(Anchor anchor)
        {
            // position 模式：世界与元系统都没被改动，无需补偿（只需保证玩家状态由 E6 复位）
            if (!IsFullScope())
            {
                Log(RestorePhase.F, "compensated");
                return true;
            }

            // 磁盘优先回滚
            bool diskOk = RollbackDisk();

            // 世界侧反向还原（反向差量表）
            await RestoreContainersFromPre();
            RestoreEnemiesFromPre();
            RestoreDoorsFromPre();
            RestoreWorldTimeFromPre();
            RestorePlayerTransformFromPre();

            _worldState = "before";
            if (diskOk) Log(RestorePhase.F, "compensated");
            else Log(RestorePhase.F, "failed");

            LogPlayerState();
            LogWorldDiff();
            LogMeta();
            LogUncovered();
            return diskOk;
        }

        private async Task RestoreContainersFromPre()
        {
            if (_pre == null) return;
            var map = LevelAccess.LootBoxInventories;
            if (map == null) return;
            foreach (var kv in _pre.Containers)
            {
                if (kv.Value?.ItemPayload == null) continue;
                if (!TryParseContainerKey(kv.Key, out int id)) continue;
                if (!map.TryGetValue(id, out var inv) || inv == null)
                {
                    AddUncovered($"container:{kv.Key}:missing");
                    continue;
                }
                bool ok = await WithTimeout(InventoryPayload.ApplyAsync(kv.Value.ItemPayload, inv), PhaseTimeoutSeconds, "F");
                if (ok) _containersChanged++;
                else AddUncovered($"container:{kv.Key}:restore-failed");
            }
        }

        /// <summary>反向差量表（回退前 B → 当前 X）。</summary>
        private void RestoreEnemiesFromPre()
        {
            if (_pre == null) return;
            var ids = new HashSet<string>();
            foreach (var kv in _pre.Enemies) ids.Add(kv.Key);
            foreach (var key in _store.EnemyKeys) ids.Add(key);

            foreach (var id in ids)
            {
                _pre.Enemies.TryGetValue(id, out var b);
                var h = GetEnemyHealth(id);
                bool missing = h == null;
                bool dead = !missing && (h.IsDead || !h.gameObject.activeSelf);
                bool bAlive = b != null && b.Alive;

                if (bAlive)
                {
                    if (missing)
                    {
                        // B 活 / X 无（对象已被销毁）：无法复活 → 记入未覆盖清单
                        AddUncovered($"enemy:{id}:destroyed");
                        continue;
                    }
                    if (dead)
                    {
                        // B 活 / X 死（含"已停用但对象还在"）：复活 + 写回 B 的状态
                        ReviveEnemy(h, b, id);
                        WriteEnemyState(h, b, id);
                    }
                    else
                    {
                        WriteEnemyState(h, b, id);
                    }
                    _enemiesChanged++;
                    continue;
                }

                // B 死 / B 无
                if (missing || dead) continue;
                h.gameObject.SetActive(false);
                _enemiesChanged++;
            }
        }

        private void RestoreDoorsFromPre()
        {
            if (_pre == null) return;
            foreach (var kv in _pre.Doors)
            {
                var door = GetDoor(kv.Key);
                if (door == null)
                {
                    AddUncovered($"door:{kv.Key}:missing");
                    continue;
                }
                bool closed = kv.Value != null && kv.Value.Closed;
                door.ForceSetClosed(closed, false);
                _doorsChanged++;
            }
        }

        private void RestoreWorldTimeFromPre()
        {
            if (_pre?.World == null) return;
            var tod = GetTimeOfDayController();
            if (tod == null)
            {
                AddUncovered("worldtime:controller-missing");
                return;
            }
            SetFloatField(tod, "time", _pre.World.TimeOfDay);
            _timeRestored = _pre.World.TimeOfDay;
        }

        private void RestorePlayerTransformFromPre()
        {
            if (_pre == null) return;
            var main = LevelManager.Instance?.MainCharacter;
            if (main == null) return;
            main.transform.position = ToVector3(_pre.PlayerPosition);
            main.transform.rotation = ToQuaternion(_pre.PlayerRotation);
        }

        // ------------------------------------------------------------------ 日志

        private static void Log(RestorePhase phase, string result)
        {
            ModLog.Log($"[DeathRewind] restore phase={phase} result={result}");
        }

        private void LogPlayerState()
        {
            float hp = 0f;
            Vector3 pos = Vector3.zero;
            int items = 0;
            bool raidDead = false;
            try
            {
                var main = LevelManager.Instance?.MainCharacter;
                if (main != null)
                {
                    if (main.Health != null) hp = main.Health.CurrentHealth;
                    pos = main.transform.position;
                    var bag = main.CharacterItem?.Inventory;
                    if (bag?.Content != null) items = bag.Content.Count;
                }
                raidDead = RaidUtilities.CurrentRaid.dead;
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 读取玩家状态失败: {e.Message}");
            }
            ModLog.Log($"[DeathRewind] restore player hp={hp} pos={pos.x},{pos.y},{pos.z} bagItems={items} raidDead={(raidDead ? "true" : "false")}");
        }

        private void LogWorldDiff()
        {
            ModLog.Log($"[DeathRewind] world diff enemies={_enemiesChanged} doorsChanged={_doorsChanged} containersChanged={_containersChanged} timeRestored={_timeRestored} worldState={_worldState}");
        }

        /// <summary>元系统摘要：EXP 与任务键。键名优先从游戏私有字段读取，读不到再用常量兜底（实施时以游戏实际键名为准）。</summary>
        private void LogMeta()
        {
            string exp = "?";
            string quests = "?";
            try
            {
                var value = SafeLoadFirst(ExpSaveKeys());
                if (value != null) exp = value.ToString();
            }
            catch { exp = "?"; }
            try
            {
                var value = SafeLoadFirst(QuestsSaveKeys());
                if (value != null) quests = Summarize(value);
            }
            catch { quests = "?"; }
            ModLog.Log($"[DeathRewind] meta exp={exp} quests={quests}");
        }

        private void LogUncovered()
        {
            ModLog.Log($"[DeathRewind] uncovered={string.Join(",", _uncovered)}");
        }

        private void DumpPreState()
        {
            if (!_config.LogVerbose) return;
            if (_pre == null) return;
            DumpState("PRE", _pre);
        }

        private void DumpPostState()
        {
            if (!_config.LogVerbose) return;
            DumpState("POST", CapturePreRewindSnapshot());
        }

        private void DumpState(string tag, PreRewindSnapshot s)
        {
            if (s == null) return;
            bool playerAlive = false;
            float playerHp = 0f;
            int playerItems = 0;
            try
            {
                var main = LevelManager.Instance?.MainCharacter;
                if (main != null)
                {
                    playerAlive = main.Health != null && !main.Health.IsDead && main.gameObject.activeSelf;
                    if (main.Health != null) playerHp = main.Health.CurrentHealth;
                    var bag = main.CharacterItem?.Inventory;
                    if (bag?.Content != null) playerItems = bag.Content.Count;
                }
            }
            catch { /* 忽略：明细日志不影响恢复流程 */ }

            ModLog.Log($"[DeathRewind][verbose] phase={tag} entity=player:main alive={playerAlive} hp={playerHp} items={playerItems}");
            foreach (var kv in s.Enemies)
            {
                var e = kv.Value;
                ModLog.Log($"[DeathRewind][verbose] phase={tag} entity=enemy:{kv.Key} alive={(e != null && e.Alive)} hp={(e != null ? e.Health : 0f)}");
            }
            foreach (var kv in s.Doors)
            {
                ModLog.Log($"[DeathRewind][verbose] phase={tag} entity=door:{kv.Key} alive=true closed={(kv.Value != null && kv.Value.Closed)}");
            }
            foreach (var kv in s.Containers)
            {
                ModLog.Log($"[DeathRewind][verbose] phase={tag} entity=container:{kv.Key} alive=true items={CountPayloadItems(kv.Value?.ItemPayload)}");
            }
        }

        private static int CountPayloadItems(byte[] payload)
        {
            try
            {
                var data = InventoryPayload.Parse(payload);
                return data?.entries?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private void AddUncovered(string item)
        {
            if (string.IsNullOrEmpty(item)) return;
            if (_uncovered.Contains(item)) return;
            _uncovered.Add(item);
        }

        // ------------------------------------------------------------------ 工具

        private static string ContainerKey(int id) => "loot:" + id;

        private static bool TryParseContainerKey(string key, out int id)
        {
            id = 0;
            if (string.IsNullOrEmpty(key)) return false;
            if (!key.StartsWith("loot:")) return false;
            return int.TryParse(key.Substring(5), out id);
        }

        private static Vector3 ToVector3(float[] v)
            => v == null || v.Length < 3 ? Vector3.zero : new Vector3(v[0], v[1], v[2]);

        private static Quaternion ToQuaternion(float[] v)
            => v == null || v.Length < 4 ? Quaternion.identity : new Quaternion(v[0], v[1], v[2], v[3]);

        private static float[] ToArray3(Vector3 v) => new[] { v.x, v.y, v.z };

        private static float[] ToArray4(Quaternion q) => new[] { q.x, q.y, q.z, q.w };

        private Health GetEnemyHealth(string id)
        {
            if (_sampler == null || string.IsNullOrEmpty(id)) return null;
            return _sampler.GetEnemyHealth(id);
        }

        private GameObject GetEnemyObject(string id)
        {
            if (_sampler == null || string.IsNullOrEmpty(id)) return null;
            return _sampler.GetEnemyObject(id);
        }

        private Door GetDoor(string id)
        {
            if (_sampler == null || string.IsNullOrEmpty(id)) return null;
            return _sampler.GetDoor(id);
        }

        private string ResolveAiTargetId(Health h)
        {
            if (h == null || _sampler == null) return null;
            var ai = h.GetComponent<AICharacterController>();
            if (ai == null || ai.searchedEnemy == null) return null;
            return _sampler.GetEnemyId(ai.searchedEnemy.gameObject);
        }

        private static TimeOfDayController GetTimeOfDayController()
        {
            try { return TimeOfDayController.Instance ?? LevelManager.Instance?.TimeOfDayController; }
            catch { return null; }
        }

        private static float ReadWorldTime()
        {
            var tod = GetTimeOfDayController();
            return tod != null ? tod.Time : 0f;
        }

        // --- 私有字段反射：统一走 Compat.PrivateStateAccessor（避免两套反射实现） ---

        private static bool GetBoolField(object target, string name)
            => Compat.PrivateStateAccessor.GetBool(target, name);

        private static void SetBoolField(object target, string name, bool value)
            => Compat.PrivateStateAccessor.SetBool(target, name, value);

        private static float GetFloatField(object target, string name)
            => Compat.PrivateStateAccessor.GetFloat(target, name);

        private static void SetFloatField(object target, string name, float value)
            => Compat.PrivateStateAccessor.SetFloat(target, name, value);

        // --- 存档键 ---

        /// <summary>RaidInfo 的存档键：优先读游戏私有静态 SaveID，兜底常量 "RaidInfo"（实施时以游戏实际键名为准）。</summary>
        private static string RaidInfoSaveKey()
        {
            try
            {
                var f = typeof(RaidUtilities).GetField("SaveID", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null)
                {
                    var v = f.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { /* 忽略 */ }
            return "RaidInfo";
        }

        /// <summary>EXP 的候选存档键（实施时以游戏实际键名为准）。</summary>
        private static List<string> ExpSaveKeys()
        {
            var keys = new List<string>();
            string prefix = ReadStaticString(typeof(Duckov.EXPManager), "prefixKey");
            string key = ReadStaticString(typeof(Duckov.EXPManager), "key");
            if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(key))
            {
                keys.Add(prefix + key);
                keys.Add(prefix + "/" + key);
            }
            if (!string.IsNullOrEmpty(key)) keys.Add(key);
            keys.Add("EXP");
            return keys;
        }

        /// <summary>任务进度的候选存档键（实施时以游戏实际键名为准）。</summary>
        private static List<string> QuestsSaveKeys()
        {
            var keys = new List<string>();
            string prefix = ReadStaticString(typeof(Duckov.Quests.QuestManager), "savePrefix");
            string key = ReadStaticString(typeof(Duckov.Quests.QuestManager), "saveKey");
            if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(key))
            {
                keys.Add(prefix + key);
                keys.Add(prefix + "/" + key);
            }
            if (!string.IsNullOrEmpty(key)) keys.Add(key);
            keys.Add("Quests");
            return keys;
        }

        private static string ReadStaticString(Type type, string fieldName)
        {
            try
            {
                var f = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                return f?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按候选键依次尝试读取存档值；读不到返回 null（不抛异常）。</summary>
        private static object SafeLoadFirst(List<string> keys)
        {
            if (keys == null) return null;
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                try
                {
                    if (!SavesSystem.KeyExisits(key)) continue;
                    var value = SavesSystem.Load<object>(key);
                    if (value != null) return value;
                }
                catch { /* 换下一个候选键 */ }
            }
            return null;
        }

        private static string Summarize(object value)
        {
            if (value == null) return "?";
            if (value is System.Collections.IDictionary dict) return "count=" + dict.Count;
            if (value is System.Collections.ICollection col) return "count=" + col.Count;
            string text = value.ToString();
            if (string.IsNullOrEmpty(text)) return "?";
            return text.Length > 64 ? text.Substring(0, 64) : text;
        }

        /// <summary>把当前 RaidInfo 写回内存态（CurrentRaid 的私有 setter 无法直接调用）。</summary>
        private static void TrySetCurrentRaid(RaidUtilities.RaidInfo info)
        {
            try
            {
                var t = typeof(RaidUtilities);
                var f = t.GetField("<CurrentRaid>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
                if (f == null) f = t.GetField("CurrentRaid", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                f?.SetValue(null, info);
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 写回 RaidInfo 内存态失败: {e.Message}");
            }
        }
    }
}
