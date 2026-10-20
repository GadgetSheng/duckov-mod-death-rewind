using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DeathRewind.Core;
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
            if (!PhaseB(anchor)) { await PhaseFAsync(anchor); return false; }
            if (!await PhaseCAsync(anchor)) { await PhaseFAsync(anchor); return false; }
            if (!PhaseD(anchor)) { await PhaseFAsync(anchor); return false; }
            if (!await PhaseEAsync(anchor)) { await PhaseE6Async(); await PhaseFAsync(anchor); return false; }

            // 成功收尾：实时状态整体替换为锚点状态（规格 §6.7）
            _store.ReplaceFrom(anchor);
            _worldState = "anchor";
            LogPlayerState();
            LogWorldDiff();
            LogMeta();
            LogUncovered();
            DumpPostState();
            return true;
        }

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

                // 1) 先刷新存档，使"回退前状态" = 磁盘 + 内存双侧一致态
                SavesSystem.CollectSaveData();
                SavesSystem.SaveFile();

                // 2) 备份主存档（路径留作字段，供阶段 B 失败与阶段 F 回滚）
                _backupPath = _meta.BackupCurrent();
                if (string.IsNullOrEmpty(_backupPath) || !File.Exists(_backupPath))
                    throw new IOException("主存档备份失败");

                // 3) 采集回退前世界快照
                _pre = CapturePreRewindSnapshot();
                DumpPreState();

                // 4) 校验锚点载荷可解析
                foreach (var kv in anchor.Containers)
                {
                    if (kv.Value?.ItemPayload == null) continue;
                    if (InventoryPayload.Parse(kv.Value.ItemPayload) == null)
                        throw new InvalidOperationException("锚点容器载荷无法解析: " + kv.Key);
                }
                if (anchor.Player?.BagPayload != null && InventoryPayload.Parse(anchor.Player.BagPayload) == null)
                    throw new InvalidOperationException("锚点玩家背包载荷无法解析");
                if (string.IsNullOrEmpty(anchor.MainSaveCopyPath) || !File.Exists(anchor.MainSaveCopyPath))
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

            var map = LevelManager.LootBoxInventories;
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
            catch (Exception e) { Debug.LogWarning($"[DeathRewind] 磁盘回滚后重载存档失败: {e.Message}"); }
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

                var map = LevelManager.LootBoxInventories;
                if (map != null)
                {
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

        // ------------------------------------------------------------------ 阶段 D

        private bool PhaseD(Anchor anchor)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (ForceFail("D")) throw new InvalidOperationException("forced failure at D");
                ApplyEnemyDiff(anchor);
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

        /// <summary>正向差量表（锚点 A → 回退前实时 B）。</summary>
        private void ApplyEnemyDiff(Anchor anchor)
        {
            var ids = new HashSet<string>();
            foreach (var kv in anchor.Enemies) ids.Add(kv.Key);
            foreach (var key in _store.EnemyKeys) ids.Add(key);

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
                        ReviveEnemy(h, a);
                    }
                    else
                    {
                        WriteEnemyState(h, a);
                    }
                    _enemiesChanged++;
                    continue;
                }

                // A 死或 A 无
                if (missing || dead) continue;   // A 死 B 死 / A 无 B 无：无操作
                h.gameObject.SetActive(false);     // 移除：只停用，销毁推迟到提交成功后
                _sampler?.MarkDeferredDestroy(id);
                _enemiesChanged++;
            }
        }

        /// <summary>写回敌人的位置/旋转/血量与 AI 目标（目标实体已不存在则置 null）。</summary>
        private void WriteEnemyState(Health h, EnemyState a)
        {
            if (h == null || a == null) return;
            WriteEnemyTransform(h, a);
            ApplyAiTarget(h, a);
        }

        /// <summary>只写回位姿与血量。</summary>
        private static void WriteEnemyTransform(Health h, EnemyState a)
        {
            if (h == null || a == null) return;
            h.transform.position = ToVector3(a.Position);
            h.transform.rotation = ToQuaternion(a.Rotation);
            try { h.SetHealth(a.Health); } catch { /* 血量写入失败不阻断差量表 */ }
        }

        /// <summary>AI 目标：目标实体仍存在则写回，否则置 null。</summary>
        private void ApplyAiTarget(Health h, EnemyState a)
        {
            if (h == null || a == null) return;
            var ai = h.GetComponent<AICharacterController>();
            if (ai == null) return;
            var targetGo = GetEnemyObject(a.AiTargetId);
            if (targetGo != null)
            {
                ai.searchedEnemy = targetGo.GetComponent<DamageReceiver>();
                ai.SetTarget(targetGo.transform);
            }
            else
            {
                ai.searchedEnemy = null;
                ai.SetTarget(null);
            }
        }

        /// <summary>复活：启用对象、清 isDead、写回血量与位姿、复位 AI（不调用 Init）。</summary>
        private static void ReviveEnemy(Health h, EnemyState a)
        {
            if (h == null) return;
            h.gameObject.SetActive(true);
            SetBoolField(h, "isDead", false);
            try { h.SetHealth(a != null ? a.Health : h.CurrentHealth); } catch { /* 忽略 */ }
            if (a != null)
            {
                h.transform.position = ToVector3(a.Position);
                h.transform.rotation = ToQuaternion(a.Rotation);
            }
            var ai = h.GetComponent<AICharacterController>();
            if (ai == null) return;
            ai.SetTarget(null);
            ai.StopMove();
            ai.noticed = false;
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
                    if (main.Health != null) main.Health.SetHealth(anchor.Player.Health);
                }

                var bag = main.CharacterItem?.Inventory;
                byte[] payload = anchor.Player?.BagPayload;
                if (bag != null && payload != null)
                {
                    bool applied = await WithTimeout(InventoryPayload.ApplyAsync(payload, bag), PhaseTimeoutSeconds, "E2");
                    if (!applied) throw new InvalidOperationException("背包原地还原失败");
                }
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
                Debug.LogWarning($"[DeathRewind] 回灌背包副本失败: {e.Message}");
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

            if (!ok)
            {
                if (error != null) Debug.LogError($"[DeathRewind] restore phase=E3 result=failed: {error}");
                else Log(RestorePhase.E3, "failed");
                return false;
            }
            Log(RestorePhase.E3, "ok");
            return true;
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
                if (lm != null) SetBoolField(lm, "dieTask", _death.IsDead);

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
            var map = LevelManager.LootBoxInventories;
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
                        ReviveEnemy(h, b);
                        WriteEnemyState(h, b);
                    }
                    else
                    {
                        WriteEnemyState(h, b);
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
            Debug.Log($"[DeathRewind] restore phase={phase} result={result}");
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
                Debug.LogWarning($"[DeathRewind] 读取玩家状态失败: {e.Message}");
            }
            Debug.Log($"[DeathRewind] restore player hp={hp} pos={pos.x},{pos.y},{pos.z} bagItems={items} raidDead={(raidDead ? "true" : "false")}");
        }

        private void LogWorldDiff()
        {
            Debug.Log($"[DeathRewind] world diff enemies={_enemiesChanged} doorsChanged={_doorsChanged} containersChanged={_containersChanged} timeRestored={_timeRestored} worldState={_worldState}");
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
            Debug.Log($"[DeathRewind] meta exp={exp} quests={quests}");
        }

        private void LogUncovered()
        {
            Debug.Log($"[DeathRewind] uncovered={string.Join(",", _uncovered)}");
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

            Debug.Log($"[DeathRewind][verbose] phase={tag} entity=player:main alive={playerAlive} hp={playerHp} items={playerItems}");
            foreach (var kv in s.Enemies)
            {
                var e = kv.Value;
                Debug.Log($"[DeathRewind][verbose] phase={tag} entity=enemy:{kv.Key} alive={(e != null && e.Alive)} hp={(e != null ? e.Health : 0f)}");
            }
            foreach (var kv in s.Doors)
            {
                Debug.Log($"[DeathRewind][verbose] phase={tag} entity=door:{kv.Key} alive=true closed={(kv.Value != null && kv.Value.Closed)}");
            }
            foreach (var kv in s.Containers)
            {
                Debug.Log($"[DeathRewind][verbose] phase={tag} entity=container:{kv.Key} alive=true items={CountPayloadItems(kv.Value?.ItemPayload)}");
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

        // --- 私有字段反射（集中在本文件内；后续 Task 12 会迁到 Compat/PrivateStateAccessor） ---

        private static FieldInfo FindField(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            Type t = target.GetType();
            while (t != null)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static bool GetBoolField(object target, string name)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissingField(name); return false; }
            try { return f.GetValue(target) is bool b && b; }
            catch (Exception e) { Debug.LogWarning($"[DeathRewind] 读取私有字段 {name} 失败: {e.Message}"); return false; }
        }

        private static void SetBoolField(object target, string name, bool value)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissingField(name); return; }
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogWarning($"[DeathRewind] 写入私有字段 {name} 失败: {e.Message}"); }
        }

        private static float GetFloatField(object target, string name)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissingField(name); return 0f; }
            try { return f.GetValue(target) is float v ? v : 0f; }
            catch (Exception e) { Debug.LogWarning($"[DeathRewind] 读取私有字段 {name} 失败: {e.Message}"); return 0f; }
        }

        private static void SetFloatField(object target, string name, float value)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissingField(name); return; }
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogWarning($"[DeathRewind] 写入私有字段 {name} 失败: {e.Message}"); }
        }

        private static void WarnMissingField(string name)
        {
            lock (_fieldWarnLock)
            {
                if (!_warnedFieldNames.Add(name)) return;
            }
            Debug.LogWarning($"[DeathRewind] 找不到私有字段 {name}");
        }

        private static readonly object _fieldWarnLock = new object();
        private static readonly HashSet<string> _warnedFieldNames = new HashSet<string>();

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
                Debug.LogWarning($"[DeathRewind] 写回 RaidInfo 内存态失败: {e.Message}");
            }
        }
    }
}
