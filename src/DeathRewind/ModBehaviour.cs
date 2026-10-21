using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeathRewind.Config;
using DeathRewind.Core;
using DeathRewind.Harmony;
using DeathRewind.UI;
using DeathRewind.Unity;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DeathRewind
{
    /// <summary>Mod 入口。游戏要求类型名为 &lt;info.ini 的 name&gt;.ModBehaviour 且继承 Duckov.Modding.ModBehaviour。</summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private ModConfig _config;
        private RewindClock _clock;
        private AnchorRing _ring;
        private LiveStateStore _store = new LiveStateStore();
        private LevelHooks _hooks;
        private EntitySampler _sampler;
        private AnchorBuilder _builder;
        private WorldRestorer _restorer;
        private double _nextSampleAt;
        private double? _levelStartTick;
        private bool _timeScaleOwned;

        // 对外暴露（DeathInterceptor 需要）
        public AnchorRing Ring => _ring;
        public double CurrentTick => _clock?.CurrentTick ?? 0;
        public ModConfig Config => _config;

        protected override void OnAfterSetup()
        {
            _config = ModConfig.Load();
            Debug.Log($"[DeathRewind] mods root: {Duckov.Modding.ModManager.DefaultModFolderPath}");
            if (!_config.Enabled) { Debug.Log("[DeathRewind] disabled by config"); return; }

            _clock = new RewindClock();
            _ring = new AnchorRing(_config.AnchorCount, _config.MinRewindAge, _config.MaxRewinds, _config.AnchorInterval);
            // 网格原点必须在 Mod 启动时就设定：否则第一条边界落在 tick=0，会立刻触发一次无意义的锚点构建
            _ring.ResetGridTo(_clock.CurrentTick, _clock.CurrentTick);
            _sampler = new EntitySampler(_store, _config.AnchorCount, _config.AnchorInterval);
            _builder = new AnchorBuilder(_config, _store, _ring);
            _restorer = new WorldRestorer(_config, _ring, _store);
            _restorer.SetSampler(_sampler);   // 恢复差量表需要按稳定 ID 找回敌人/门对象

            // 启动时清理孤儿文件（文件名不对应任何现存锚点）
            AnchorBuilder.CleanupOrphanFiles(new HashSet<int>());

            _hooks = new LevelHooks();
            _hooks.OnLevelStarted += HandleLevelStarted;
            _hooks.OnSubSceneLoaded += HandleSubSceneLoaded;
            _hooks.OnLevelEnded += HandleLevelEnded;
            _hooks.Install();

            DeathInterceptor.Install(this, _config, _restorer);
            EnemyPersistencePatch.Install(_sampler);
            Debug.Log("[DeathRewind] initialized");
        }

        protected override void OnBeforeDeactivate()
        {
            ReleaseTimeScale();
            DeathInterceptor.Uninstall();
            EnemyPersistencePatch.Uninstall();
            _hooks?.Uninstall();
            Debug.Log("[DeathRewind] deactivated");
        }

        private void Update()
        {
            if (_config == null || !_config.Enabled) return;
            _clock.Tick(Time.unscaledTime);
            _ring.SetTick(_clock.CurrentTick);

            // 冻结期间：不采样、不推进网格（规格 §6.2/§6.3/§7 步骤 3）
            if (!_clock.IsFrozen)
            {
                if (_clock.CurrentTick >= _nextSampleAt)
                {
                    _nextSampleAt = _clock.CurrentTick + _config.SampleInterval;
                    _sampler.Sample(_clock.CurrentTick);
                }

                var due = _ring.PollDueGridTimestamp();
                if (due.HasValue && LevelAccess.LevelReady)
                {
                    int id = _ring.AllocateId();
                    var sw = Stopwatch.StartNew();
                    var anchor = _builder.Build(id, _clock.CurrentTick, due.Value);
                    sw.Stop();
                    var evicted = new List<Anchor>();
                    _ring.Insert(anchor, evicted);
                    foreach (var old in evicted) _sampler.ReleaseAnchor(old);
                    _sampler.SetAnchorCoverage(_ring.Anchors);
                    Debug.Log($"[DeathRewind] anchor created id={anchor.Id} gridTs={anchor.GridTimestamp} absTs={anchor.AbsTimestamp} buildMs={sw.ElapsedMilliseconds}");
                }
            }

            HandlePromptHotkeys();
            HandleDebugHotkeys();
        }

        /// <summary>IMGUI 只能在 OnGUI 中调用（Update 里调用会每帧抛异常）。</summary>
        private void OnGUI()
        {
            RewindPrompt.Draw();
        }

        private void HandleLevelStarted()
        {
            // 时钟不复位（有效 tick 必须单调），只重置网格原点与状态
            _ring.Clear();
            _ring.ResetGridTo(_clock.CurrentTick, _clock.CurrentTick);
            _store.Clear();
            _sampler.ResetRegistry();
            _nextSampleAt = _clock.CurrentTick;
            _levelStartTick = _clock.CurrentTick;
            Debug.Log($"[DeathRewind] level started: anchors/rewindCount/store reset at tick={_clock.CurrentTick}");
        }

        private void HandleSubSceneLoaded()
        {
            // 只刷新注册表，不清锚点环、不归零回退计数（规格 §8.2）
            _sampler.RefreshRegistry();
            Debug.Log($"[DeathRewind] subscene loaded: anchors={_ring.Anchors.Count} rewindCount={_ring.RewindCount}");
        }

        private void HandleLevelEnded()
        {
            ReleaseTimeScale();
            _ring.Clear();
            _store.Clear();
            _sampler.DestroyAllTrackedEnemies();
            _sampler.ResetRegistry();
            AnchorBuilder.DeleteAllAnchorFiles();
            Debug.Log("[DeathRewind] level ended: state cleared");
        }

        private void HandlePromptHotkeys()
        {
            if (!RewindPrompt.IsOpen) return;
            if (Input.GetKeyDown(ParseKey(_config.RewindHotkey))) RewindPrompt.Submit(true);
            if (Input.GetKeyDown(ParseKey(_config.ContinueHotkey))) RewindPrompt.Submit(false);
        }

        private void HandleDebugHotkeys()
        {
            if (!_config.DebugMode) return;
            if (Input.GetKeyDown(KeyCode.F10)) TriggerDebugDeath();
            if (_config.DebugAutoDeathAfter > 0 && _levelStartTick.HasValue)
            {
                double elapsed = _clock.CurrentTick - _levelStartTick.Value;
                if (elapsed >= _config.DebugAutoDeathAfter)
                {
                    _levelStartTick = null;   // 只触发一次
                    Debug.Log($"[DeathRewind] debug auto death after {elapsed:0.0}s");
                    TriggerDebugDeath();
                }
            }
        }

        /// <summary>调试用：给自己造成致命伤害，走完整死亡链路。</summary>
        private void TriggerDebugDeath()
        {
            var player = LevelManager.Instance?.MainCharacter;
            if (player == null) return;
            var health = player.Health;
            if (health == null) return;
            var dmg = new DamageInfo
            {
                damageValue = 1e6f,
                finalDamage = 1e6f,
                fromCharacter = player,
                toDamageReceiver = player.GetComponentInChildren<DamageReceiver>() ?? player.GetComponent<DamageReceiver>(),
                damagePoint = player.transform.position,
            };
            try
            {
                health.Hurt(dmg);
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 调试致死经 Health.Hurt 失败（{e.Message}），改用直接触发死亡事件");
            }
            // 兜底：与 Health.Hurt 的死亡分支等价 —— 置零血量、置死亡标记、触发 OnDeadEvent
            try
            {
                health.SetHealth(0f);
                Compat.PrivateStateAccessor.SetBool(health, "isDead", true);
                health.OnDeadEvent?.Invoke(dmg);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 调试致死兜底失败: {e}");
            }
        }

        private static KeyCode ParseKey(string name)
            => string.IsNullOrWhiteSpace(name) ? KeyCode.None : (KeyCode)System.Enum.Parse(typeof(KeyCode), name, true);

        // timeScale 收口：任何退出拦截面板的路径都必须经过这里
        public void FreezeTimeScale()
        {
            Time.timeScale = 0f;
            _timeScaleOwned = true;
            _clock?.Freeze();
        }

        public void ReleaseTimeScale()
        {
            // 计划写法在 !_timeScaleOwned 时直接返回；这里补上时钟的无条件 Resume，
            // 保证"退出拦截面板"的任何路径都能让有效 tick 重新推进（评审要点 2）。
            if (_timeScaleOwned)
            {
                _timeScaleOwned = false;
                Time.timeScale = 1f;
            }
            _clock?.Resume();
        }

        // 供 DeathInterceptor 调用
        public void ReleaseAnchor(Anchor anchor) => _sampler?.ReleaseAnchor(anchor);
        public void ReleaseCommittedDestroy() => _sampler?.DestroyDeferredEnemies();
    }
}
