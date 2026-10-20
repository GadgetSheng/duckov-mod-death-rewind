using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DeathRewind.Core;
using DeathRewind.UI;
using DeathRewind.Unity;
using HarmonyLib;
using HarmonyLibHarmony = HarmonyLib.Harmony;
using Saves;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>
    /// 死亡拦截：冻结时间 → 裁决（自动回退或确认面板）→ 放行/重放/回退。
    /// 所有退出路径都必须收口（timeScale 复位、拦截标志清除、面板关闭）。
    /// </summary>
    public static class DeathInterceptor
    {
        private static ModBehaviour _owner;
        private static Config.ModConfig _config;
        private static WorldRestorer _restorer;
        private static bool _interceptionActive;
        private static bool _bypassNext;          // 仅用于"下一帧重放"路径
        private static DamageInfo _capturedDamage;
        private static DeathSnapshot _deathSnapshot;
        private static HarmonyLibHarmony _harmony;

        public static void Install(ModBehaviour owner, Config.ModConfig config, WorldRestorer restorer)
        {
            _owner = owner; _config = config; _restorer = restorer;
            if (!HarmonyGuard.CanRegister("com.deathrewind.interceptor"))
            {
                Debug.LogWarning("[DeathRewind] 因 Harmony 冲突，拦截功能未启用");
                return;
            }
            var target = AccessTools.Method(typeof(LevelManager), "OnMainCharacterDie", new[] { typeof(DamageInfo) });
            if (target == null)
            {
                Debug.LogError("[DeathRewind] 未找到 LevelManager.OnMainCharacterDie，拦截不可用");
                return;
            }
            _harmony = new HarmonyLibHarmony("com.deathrewind.interceptor");
            var prefix = AccessTools.Method(typeof(DeathInterceptor), "Prefix", new[] { typeof(DamageInfo) });
            if (prefix == null) { Debug.LogError("[DeathRewind] 未找到 DeathInterceptor.Prefix"); return; }
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        public static void Uninstall()
        {
            _harmony?.UnpatchAll("com.deathrewind.interceptor");
            _harmony = null;
            _interceptionActive = false;
            _bypassNext = false;
            RewindPrompt.Close();
        }

        /// <summary>前缀必须是同步方法。用 __0 取原始形参，避免依赖原方法形参名。</summary>
        private static bool Prefix(DamageInfo __0)
        {
            if (_config == null || !_config.Enabled) return true;

            // 路径一：下一帧重放（"继续"或回退失败）——放行这一次
            if (_bypassNext)
            {
                _bypassNext = false;
                return true;
            }

            // 路径二：冻结期间的二次死亡——取消待定回退并放行当前这次（**不重放**、不再设 _bypassNext）
            if (_interceptionActive)
            {
                CancelPending();
                return true;
            }

            // 准入判定（规格 §6.4）：三条全部满足才拦截。AutoRewind 只影响是否显示面板。
            var ring = _owner != null ? _owner.Ring : null;
            if (ring == null) return true;
            if (!ring.CanRewind())
            {
                Debug.Log(ring.RewindCount >= _config.MaxRewinds
                    ? "[DeathRewind] rewind limit reached"
                    : "[DeathRewind] no eligible anchor");
                return true;
            }

            _interceptionActive = true;
            _capturedDamage = __0;
            _deathSnapshot = CaptureDeathSnapshot();
            _restorer?.SetDeathContext(_deathSnapshot, _capturedDamage);
            _owner.FreezeTimeScale();
            Debug.Log($"[DeathRewind] intercept at gridTime={_owner.CurrentTick} finalDamage={_capturedDamage.finalDamage}");

            if (_config.AutoRewind)
            {
                Submit(true);
            }
            else
            {
                RewindPrompt.Open(Submit);
            }
            return false;   // 吞掉原方法
        }

        /// <summary>面板裁决：true = 回退，false = 接受死亡。</summary>
        private static void Submit(bool rewind)
        {
            if (!_interceptionActive) return;
            try
            {
                if (rewind)
                {
                    var anchor = _owner.Ring.SelectTarget();
                    if (anchor == null)
                    {
                        Debug.Log("[DeathRewind] no eligible anchor");
                        _interceptionActive = false;
                        _owner.ReleaseTimeScale();
                        ScheduleReplay();
                        return;
                    }
                    Debug.Log($"[DeathRewind] anchor selected id={anchor.Id} anchorTs={anchor.AbsTimestamp} age={_owner.CurrentTick - anchor.AbsTimestamp} " +
                              $"anchorHp={anchor.Player?.Health} anchorPos={Format(anchor.Player?.Position)} anchorBagItems={CountItems(anchor.Player?.BagPayload)}");
                    _owner.StartCoroutine(RestoreRoutine(anchor));
                }
                else
                {
                    Debug.Log("[DeathRewind] player chose to accept death");
                    // 这条路径不会进入 RestoreRoutine，必须在这里自行收口：
                    // 解除冻结 + 清拦截标志，否则游戏会永久停在 0 倍速
                    _interceptionActive = false;
                    _owner.ReleaseTimeScale();
                    ScheduleReplay();
                }
            }
            finally
            {
                RewindPrompt.Close();
            }
        }

        private static IEnumerator RestoreRoutine(Anchor anchor)
        {
            bool ok = false;
            System.Threading.Tasks.Task<bool> task = null;
            try
            {
                task = _restorer.RestoreAsync(anchor);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 回退异常: {e}");
                ok = false;
            }

            if (task != null)
            {
                while (!task.IsCompleted) yield return null;
                try
                {
                    ok = task.Result;      // 阶段内部已 try/catch；此处仍需 try 防止 AggregateException
                }
                catch (Exception e)
                {
                    Debug.LogError($"[DeathRewind] 回退异常: {e}");
                    ok = false;
                }
            }

            if (ok)
            {
                var removed = new List<Anchor>();
                _owner.Ring.CommitRewind(anchor, removed);
                _owner.Ring.ResetGridTo(anchor.GridTimestamp, _owner.CurrentTick);
                foreach (var old in removed) _owner.ReleaseAnchor(old);
                _owner.ReleaseCommittedDestroy();
                Debug.Log($"[DeathRewind] rewind result=ok rewindCount={_owner.Ring.RewindCount} consumedAnchor={anchor.Id}");
            }
            else
            {
                Debug.LogWarning($"[DeathRewind] rewind result=failed rewindCount={_owner.Ring.RewindCount}");
                ScheduleReplay();     // 回退失败 → 放行死亡流程，避免卡死
            }

            _interceptionActive = false;
            _owner.ReleaseTimeScale();
            RewindPrompt.Close();
        }

        /// <summary>下一帧反射调用原方法，复用捕获的原始 DamageInfo。</summary>
        private static void ScheduleReplay()
        {
            _bypassNext = true;
            var target = AccessTools.Method(typeof(LevelManager), "OnMainCharacterDie", new[] { typeof(DamageInfo) });
            var damage = _capturedDamage;
            _owner.StartCoroutine(ReplayNextFrame(target, damage));
        }

        private static IEnumerator ReplayNextFrame(MethodInfo target, DamageInfo damage)
        {
            yield return null;   // 下一帧
            if (target == null) yield break;
            try { target.Invoke(LevelManager.Instance, new object[] { damage }); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 重放死亡流程失败: {e}"); }
        }

        private static void CancelPending()
        {
            RewindPrompt.Close();
            _owner?.ReleaseTimeScale();
            _interceptionActive = false;      // 必须清：否则本关卡后续每次死亡都走"二次进入"分支
        }

        /// <summary>拦截时刻直接读取的玩家死亡态（反向复位的唯一权威来源）。</summary>
        private static DeathSnapshot CaptureDeathSnapshot()
        {
            var player = LevelManager.Instance?.MainCharacter;
            var snap = new DeathSnapshot();
            if (player == null) return snap;
            var health = player.Health;
            snap.Health = health.CurrentHealth;
            snap.IsDead = health.IsDead;
            snap.HandledDeath = Compat.PrivateStateAccessor.GetBool(player, "hasHandledDeath");
            snap.ActiveSelf = player.gameObject.activeSelf;
            snap.Invincible = health.Invincible;
            snap.RaidDead = RaidUtilities.CurrentRaid.dead;
            snap.Position = new[] { player.transform.position.x, player.transform.position.y, player.transform.position.z };
            snap.Rotation = new[] { player.transform.rotation.x, player.transform.rotation.y, player.transform.rotation.z, player.transform.rotation.w };
            snap.BagPayload = InventoryPayload.Capture(player.CharacterItem.Inventory);
            return snap;
        }

        private static string Format(float[] v) => v == null ? "-" : $"{v[0]:0.0},{v[1]:0.0},{v[2]:0.0}";
        private static int CountItems(byte[] payload)
        {
            var data = InventoryPayload.Parse(payload);
            return data?.entries?.Count ?? 0;
        }
    }
}
