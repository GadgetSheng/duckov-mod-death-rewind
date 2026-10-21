using System.Collections.Generic;
using DeathRewind.Core;
using ItemStatsSystem;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>枚举关卡实体 → DTO；维护稳定 ID 注册表；敌人登记、延迟销毁、出环释放与超时淘汰。</summary>
    public sealed class EntitySampler
    {
        private readonly LiveStateStore _store;
        private readonly int _anchorCount;
        private readonly double _anchorInterval;
        private readonly Dictionary<Health, string> _enemyIds = new Dictionary<Health, string>();
        private readonly Dictionary<GameObject, string> _enemyObjects = new Dictionary<GameObject, string>();
        private readonly Dictionary<Health, AICharacterController> _enemyControllers = new Dictionary<Health, AICharacterController>();
        private readonly Dictionary<DamageReceiver, string> _receiverIds = new Dictionary<DamageReceiver, string>();
        private readonly HashSet<Item> _enemyItems = new HashSet<Item>();
        private readonly Dictionary<Door, string> _doorIds = new Dictionary<Door, string>();
        private readonly Dictionary<string, double> _registeredAt = new Dictionary<string, double>();
        private readonly HashSet<string> _anchorCovered = new HashSet<string>();
        private readonly HashSet<string> _deferredDestroy = new HashSet<string>();
        private int _enemySeq;
        private int _doorSeq;
        private double _now;

        public EntitySampler(LiveStateStore store, int anchorCount, double anchorInterval)
        {
            _store = store;
            _anchorCount = anchorCount;
            _anchorInterval = anchorInterval;
        }

        /// <summary>敌人判定（与 EnemyPersistencePatch 共用）。</summary>
        public static bool IsEnemyCharacter(Health health)
        {
            if (health == null) return false;
            if (health.IsMainCharacterHealth) return false;
            return FindAiController(health) != null;
        }

        public void Sample(double now)
        {
            _now = now;
            SamplePlayer();
            SampleEnemies();
            SampleContainers();
            SampleDoors();
            EvictStale();
        }

        private void SamplePlayer()
        {
            var main = LevelManager.Instance?.MainCharacter;
            if (main == null) return;
            _store.SetPlayer(new PlayerState
            {
                Position = ToArray3(main.transform.position),
                Rotation = ToArray4(main.transform.rotation),
                Health = main.Health.CurrentHealth,
                Alive = !main.Health.IsDead,
                BagPayload = InventoryPayload.Capture(main.CharacterItem.Inventory),
            });
        }

        private void SampleEnemies()
        {
            // 1) 发现新实体 —— 敌人可能把 Health 与 AI 控制器放在不同 GameObject 上，
            //    所以两条路都走：① 枚举 AICharacterController（主力）② 枚举 Health（兜底）
            foreach (var ai in Object.FindObjectsByType<AICharacterController>(FindObjectsSortMode.None))
            {
                var character = Compat.PrivateStateAccessor.GetObject(ai, "characterMainControl") as CharacterMainControl;
                var health = character?.Health;
                if (health == null) continue;
                RegisterEnemy(health, ai);
            }
            foreach (var health in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
            {
                if (health == null || health.IsMainCharacterHealth) continue;
                RegisterEnemy(health, FindAiController(health));
            }

            // 2) 更新状态（必须遍历注册表，否则停用的敌人观测不到）
            foreach (var kv in new List<KeyValuePair<Health, string>>(_enemyIds))
            {
                var health = kv.Key;
                if (health == null) { RemoveEntry(kv.Value, kv.Key); continue; }
                var ai = _enemyControllers.TryGetValue(health, out var ctrl) ? ctrl : FindAiController(health);
                _store.SetEnemy(kv.Value, new EnemyState
                {
                    Position = ToArray3(health.transform.position),
                    Rotation = ToArray4(health.transform.rotation),
                    Health = health.CurrentHealth,
                    Alive = !health.IsDead,
                    AiTargetId = ResolveTargetId(ai),
                    Noticed = ai != null && ai.noticed,
                });
            }
        }

        /// <summary>登记一个敌人（分配稳定 ID、记录 AI 控制器与 DamageReceiver）。</summary>
        private void RegisterEnemy(Health health, AICharacterController ai)
        {
            if (health == null || health.IsMainCharacterHealth) return;
            if (_enemyIds.ContainsKey(health)) return;
            string id = "enemy:" + (++_enemySeq);
            _enemyIds[health] = id;
            _enemyObjects[health.gameObject] = id;
            _registeredAt[id] = _now;
            if (ai != null) _enemyControllers[health] = ai;
            var receiver = FindDamageReceiver(health);
            if (receiver != null) _receiverIds[receiver] = id;
            // 记录敌人的 CharacterItem：敌人死亡时游戏会把装备丢进尸体箱，
            // 复活后就没装备了。这里登记后由 EnemyLootboxPatch 阻止那次丢弃。
            var character = CharacterOf(health);
            if (character != null && character.CharacterItem != null) _enemyItems.Add(character.CharacterItem);
        }

        /// <summary>取某个 Health 所属的角色。</summary>
        private static CharacterMainControl CharacterOf(Health health)
        {
            if (health == null) return null;
            try { return health.TryGetCharacter(); } catch { return null; }
        }

        /// <summary>该物品是否是某个敌人的角色物品（用于阻止敌人死亡时丢装备）。</summary>
        public bool IsEnemyItem(Item item)
        {
            if (item == null) return false;
            return _enemyItems.Contains(item);
        }

        /// <summary>
        /// 找到敌人身上的 AI 控制器。Health 与 AICharacterController 可能在不同 GameObject 上，
        /// 因此依次尝试：同物体 → 角色自身 → 角色子物体。
        /// </summary>
        public static AICharacterController FindAiController(Health health)
        {
            if (health == null) return null;
            var ai = health.GetComponent<AICharacterController>();
            if (ai != null) return ai;
            CharacterMainControl character = null;
            try { character = health.TryGetCharacter(); } catch { /* 忽略 */ }
            if (character == null) return null;
            ai = character.GetComponent<AICharacterController>();
            if (ai != null) return ai;
            return character.GetComponentInChildren<AICharacterController>();
        }

        /// <summary>找到 Health 所属角色身上的 DamageReceiver（AI 索敌用的就是它）。</summary>
        public static DamageReceiver FindDamageReceiver(Health health)
        {
            if (health == null) return null;
            var receiver = health.GetComponent<DamageReceiver>();
            if (receiver != null) return receiver;
            var character = health.TryGetCharacter();
            if (character == null) return null;
            return character.GetComponent<DamageReceiver>() ?? character.GetComponentInChildren<DamageReceiver>();
        }

        private void SampleContainers()
        {
            // LootBoxInventories 是静态属性（反编译确认），不能用实例引用访问
            var map = LevelAccess.LootBoxInventories;
            if (map == null) return;
            foreach (var kv in map)
            {
                string key = "loot:" + kv.Key;
                var inv = kv.Value;
                if (inv == null) continue;
                _store.SetContainer(key, new ContainerState
                {
                    Key = key,
                    Capacity = inv.Capacity,
                    LockedIndexes = inv.lockedIndexes != null ? inv.lockedIndexes.ToArray() : new int[0],
                    ItemPayload = InventoryPayload.Capture(inv),
                });
            }
        }

        private void SampleDoors()
        {
            foreach (var door in Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                if (door == null) continue;
                if (!_doorIds.TryGetValue(door, out var id))
                {
                    id = "door:" + (++_doorSeq);
                    _doorIds[door] = id;
                }
                _store.SetDoor(id, new DoorState { Key = id, Closed = !door.IsOpen });
            }
        }

        /// <summary>敌人销毁接管时登记：分配稳定 ID、标记死亡，销毁推迟到提交成功后。</summary>
        public void RegisterDeadEnemy(Health health)
        {
            if (health == null) return;
            if (_enemyIds.TryGetValue(health, out var existing))
            {
                _store.SetEnemy(existing, new EnemyState { Alive = false, Health = 0f });
                return;
            }
            string id = "enemy:" + (++_enemySeq);
            _enemyIds[health] = id;
            _enemyObjects[health.gameObject] = id;
            _registeredAt[id] = _now;
            _store.SetEnemy(id, new EnemyState
            {
                Alive = false,
                Health = 0f,
                Position = ToArray3(health.transform.position),
                Rotation = ToArray4(health.transform.rotation),
            });
        }

        /// <summary>回退中"移除"的敌人：只停用，销毁推迟到提交成功后。</summary>
        public void MarkDeferredDestroy(string enemyId) => _deferredDestroy.Add(enemyId);

        // --- 注册表查询（供 WorldRestorer 的差量表按稳定 ID 找回对象） ---

        /// <summary>按稳定 ID 找回敌人的 Health；对象已被游戏销毁时返回 null。</summary>
        public Health GetEnemyHealth(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var kv in _enemyObjects)
            {
                if (kv.Value != id) continue;
                if (kv.Key == null) return null;
                var health = kv.Key.GetComponent<Health>();
                if (health != null) return health;
            }
            return null;
        }

        /// <summary>按稳定 ID 找回敌人的 GameObject；对象已被游戏销毁时返回 null。</summary>
        public GameObject GetEnemyObject(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var kv in _enemyObjects)
            {
                if (kv.Value != id) continue;
                return kv.Key;
            }
            return null;
        }

        /// <summary>反向查询：GameObject → 稳定 ID；不在注册表内时返回 null。</summary>
        public string GetEnemyId(GameObject go)
        {
            if (go == null) return null;
            foreach (var kv in _enemyObjects)
            {
                if (kv.Key == go) return kv.Value;
            }
            return null;
        }

        /// <summary>按稳定 ID 找回门；对象已被销毁时返回 null。</summary>
        public Door GetDoor(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var kv in _doorIds)
            {
                if (kv.Value != id) continue;
                return kv.Key;
            }
            return null;
        }

        /// <summary>提交成功：销毁本次回退中被移除的敌人对象（失败路径不调用，保持可反向激活）。</summary>
        public void DestroyDeferredEnemies()
        {
            foreach (var id in _deferredDestroy)
            {
                foreach (var kv in _enemyObjects)
                {
                    if (kv.Value != id) continue;
                    if (kv.Key != null && !kv.Key.activeSelf) Object.Destroy(kv.Key);
                }
            }
            _deferredDestroy.Clear();
        }

        /// <summary>关卡结束：销毁全部被跟踪的停用敌人对象。</summary>
        public void DestroyAllTrackedEnemies()
        {
            foreach (var kv in _enemyObjects)
                if (kv.Key != null && !kv.Key.activeSelf) Object.Destroy(kv.Key);
            _deferredDestroy.Clear();
        }

        /// <summary>锚点出环：删除该锚点的载荷/存档文件，并释放它覆盖过的停用敌人对象。</summary>
        public void ReleaseAnchor(Anchor anchor)
        {
            if (anchor == null) return;
            AnchorBuilder.DeleteAnchorFiles(anchor);
            foreach (var kv in _enemyObjects)
            {
                if (kv.Value == null) continue;
                if (!anchor.Enemies.ContainsKey(kv.Value)) continue;
                var go = kv.Key;
                if (go == null) continue;
                if (!go.activeSelf) Object.Destroy(go);
            }
        }

        /// <summary>记录当前锚点覆盖了哪些敌人（用于淘汰判定：被锚点覆盖的敌人不能淘汰）。</summary>
        public void SetAnchorCoverage(IReadOnlyList<Anchor> anchors)
        {
            _anchorCovered.Clear();
            if (anchors == null) return;
            foreach (var a in anchors)
                foreach (var id in a.Enemies.Keys)
                    _anchorCovered.Add(id);
        }

        /// <summary>未被任何锚点覆盖、且登记超过 anchorCount×anchorInterval 的停用敌人 → 淘汰销毁。</summary>
        private void EvictStale()
        {
            double limit = _anchorCount * _anchorInterval;
            var stale = new List<string>();
            foreach (var kv in _registeredAt)
            {
                if (_anchorCovered.Contains(kv.Key)) continue;      // 锚点还要用它复活
                if (_now - kv.Value > limit) stale.Add(kv.Key);
            }
            foreach (var id in stale)
            {
                _registeredAt.Remove(id);
                foreach (var kv in new List<KeyValuePair<GameObject, string>>(_enemyObjects))
                {
                    if (kv.Value != id) continue;
                    if (kv.Key != null && !kv.Key.activeSelf) Object.Destroy(kv.Key);
                    _enemyObjects.Remove(kv.Key);
                }
            }
        }

        public void ResetRegistry()
        {
            _enemyIds.Clear();
            _enemyObjects.Clear();
            _doorIds.Clear();
            _registeredAt.Clear();
            _anchorCovered.Clear();
            _deferredDestroy.Clear();
        }

        /// <summary>子场景加载：剔除已销毁对象的引用，保留其余稳定 ID。</summary>
        public void RefreshRegistry()
        {
            foreach (var kv in new List<KeyValuePair<Health, string>>(_enemyIds))
                if (kv.Key == null) RemoveEntry(kv.Value, kv.Key);
            foreach (var kv in new List<KeyValuePair<Door, string>>(_doorIds))
                if (kv.Key == null) _doorIds.Remove(kv.Key);
        }

        private void RemoveEntry(string id, Health key)
        {
            _registeredAt.Remove(id);
            if (key != null) _enemyIds.Remove(key);
            foreach (var kv in new List<KeyValuePair<GameObject, string>>(_enemyObjects))
                if (kv.Value == id) _enemyObjects.Remove(kv.Key);
        }

        private string ResolveTargetId(AICharacterController ai)
        {
            if (ai == null) return null;
            var target = ai.searchedEnemy;
            if (target == null) return null;
            // 目标可能是玩家（DamageReceiver 不在敌人注册表里）
            var player = LevelAccess.MainCharacter;
            if (player != null)
            {
                var playerReceiver = FindDamageReceiver(player.Health);
                if (playerReceiver != null && playerReceiver == target) return "player";
            }
            return _receiverIds.TryGetValue(target, out var id) ? id : null;
        }

        /// <summary>按稳定 ID 取敌人的 AI 控制器（回退时写回索敌状态用）。</summary>
        public AICharacterController GetEnemyAi(string id)
        {
            foreach (var kv in _enemyIds)
                if (kv.Value == id)
                {
                    if (kv.Key == null) return null;
                    if (_enemyControllers.TryGetValue(kv.Key, out var ai)) return ai;
                    return FindAiController(kv.Key);
                }
            return null;
        }

        /// <summary>按稳定 ID 取敌人的 DamageReceiver（AI 索敌对象）。</summary>
        public DamageReceiver GetEnemyReceiver(string id)
        {
            foreach (var kv in _enemyIds)
                if (kv.Value == id && kv.Key != null) return FindDamageReceiver(kv.Key);
            return null;
        }

        /// <summary>按稳定 ID 取敌人角色（采集物品载荷用）；对象已销毁时返回 null。</summary>
        public CharacterMainControl GetEnemyCharacter(string id)
        {
            var h = GetEnemyHealth(id);
            if (h == null) return null;
            try { return h.TryGetCharacter(); }
            catch { return null; }
        }

        /// <summary>玩家的 DamageReceiver（敌人的索敌对象通常是它）。</summary>
        public DamageReceiver GetPlayerReceiver()
        {
            var player = LevelAccess.MainCharacter;
            return player == null ? null : FindDamageReceiver(player.Health);
        }

        private static float[] ToArray3(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] ToArray4(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    }
}
