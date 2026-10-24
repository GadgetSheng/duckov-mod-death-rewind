using System;
using System.Collections;
using System.Collections.Generic;
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
        private AnchorBuildContext _pendingBuild;
        private double _nextSampleAt;
        private double? _levelStartTick;
        private readonly Dictionary<string, Anchor> _levelStartAnchors = new Dictionary<string, Anchor>();
        private readonly HashSet<string> _scenesWithStartAnchor = new HashSet<string>();

        /// <summary>场景切换后的静置窗口（秒）：游戏在 LoadSubScene 返回后才把玩家传送到入口位置，
        /// 期间采集的起点锚点会记到传送前的坐标。场景名变化/加载结束后的这段时间内不建锚点。</summary>
        private const double StartAnchorSettleSeconds = 0.75;
        private const double LoadingSettleSeconds = 0.35;
        private double _startAnchorNotBeforeTick;
        private string _lastSceneKey;

        /// <summary>当前关卡的"主要"游玩场景（首次稳定后的 active scene）。
        /// 仅作日志标识；建锚点 gate 不再依赖单一 mapSceneKey，而是允许任何**稳定停留**的场景
        /// （包括实验室 JLab 的楼层 1/2、HiddenWarehouse 的主层等 addtive 游玩层）。
        /// 地道/室内短暂切换靠 MultiSceneLoading + StartAnchorSettleSeconds 自然过滤。</summary>
        private string _mapSceneKey;
        private bool _timeScaleOwned;

        // 对外暴露（DeathInterceptor 需要）
        public AnchorRing Ring => _ring;
        public double CurrentTick => _clock?.CurrentTick ?? 0;

        /// <summary>死亡裁决期间置 true：禁止 OnLevelEnded 清空锚点环（楼层切换时 RaidEnd/场景 unload 可能误触发）。</summary>
        public bool SuppressLevelEnd
        {
            get => _hooks?.SuppressLevelEnd ?? false;
            set { if (_hooks != null) _hooks.SuppressLevelEnd = value; }
        }

        /// <summary>指定场景的"起点锚点"（进入该场景那一刻的状态）。每个子场景（楼层/地道）独立一个。</summary>
        public Anchor GetLevelStartAnchor(string sceneKey)
            => sceneKey != null && _levelStartAnchors.TryGetValue(sceneKey, out var a) ? a : null;

        protected override void OnAfterSetup()
        {
            ModLog.Log("[DR-Trace] ====== Log channel test: if you see this, Log works ======");
            _config = ModConfig.Load();
            ModLog.Log($"[DeathRewind] mods root: {Duckov.Modding.ModManager.DefaultModFolderPath}");
            RegisterModConfigOptions();
            if (!_config.Enabled) { ModLog.Log("[DeathRewind] disabled by config"); return; }

            _clock = new RewindClock();
            _ring = new AnchorRing(_config.AnchorCount, _config.MaxRewindAge, _config.MaxRewinds, _config.AnchorInterval);
            // 网格原点必须在 Mod 启动时就设定：否则第一条边界落在 tick=0，会立刻触发一次无意义的锚点构建
            _ring.ResetGridTo(_clock.CurrentTick, _clock.CurrentTick);
            _sampler = new EntitySampler(_store, _config.AnchorCount, _config.AnchorInterval);
            _builder = new AnchorBuilder(_config, _store, _ring, _sampler, CaptureAnchorThumbnail);   // 锚点采集需要按 ID 找回敌人物品
            _restorer = new WorldRestorer(_config, _ring, _store);
            _restorer.SetSampler(_sampler);   // 恢复差量表需要按稳定 ID 找回敌人/门对象

            // 启动时清理孤儿文件（文件名不对应任何现存锚点）
            AnchorBuilder.CleanupOrphanFiles(new HashSet<int>());

            _hooks = new LevelHooks();
            _hooks.OnLevelStarted += HandleLevelStarted;
            _hooks.OnSubSceneLoaded += HandleSubSceneLoaded;
            _hooks.OnLevelEnded += HandleLevelEnded;
            _hooks.Install();

            try
            {
                DeathInterceptor.Install(this, _config, _restorer);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] DeathInterceptor install failed: {e}");
            }
            try
            {
                // 伤害门控诊断补丁：仅调试构建启用，用于抓“复活后无敌”的外部来源
                if (ModLog.USE_DEBUG) HurtGatePatch.Install();
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] HurtGatePatch install failed: {e}");
            }
            try
            {
                // 敌人相关的两个补丁只在"完整世界回滚"模式下需要：
            // position 模式不复活敌人 → 敌人应当按原生逻辑正常死亡（消失 + 留下可搜刮的尸体箱）
            if (string.Equals(_config.RewindScope, "full", System.StringComparison.OrdinalIgnoreCase))
            {
                EnemyPersistencePatch.Install(_sampler);
                EnemyLootboxPatch.Install(_sampler);
            }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] EnemyPersistencePatch install failed: {e}");
            }
            // 缩略图改为锚点构建同帧抓取（见 CaptureAnchorThumbnail），不再需要端帧循环
            ModLog.Log("[DeathRewind] initialized");
        }

        /// <summary>
        /// 接入 ModConfig 框架（游戏内设置界面）；框架未安装时为空操作。
        /// 热键等复杂项不在界面暴露，继续走 config.txt。
        /// </summary>
        private void RegisterModConfigOptions()
        {
            if (!ModConfigBridge.Available) return;
            ModConfigBridge.AddBool("enabled", "启用死亡回溯", _config.Enabled);
            ModConfigBridge.AddDropdown("rewindScope", "回溯范围", new[] { "position", "full" },
                new[] { "仅自身状态（推荐）", "完整世界回滚（重）" }, _config.RewindScope);
            ModConfigBridge.AddSlider("anchorInterval", "锚点间隔（秒）", typeof(float), (float)_config.AnchorInterval, new Vector2(1f, 30f));
            ModConfigBridge.AddSlider("anchorCount", "锚点数量（窗口=数量×间隔）", typeof(int), _config.AnchorCount, new Vector2(4f, 64f));
            ModConfigBridge.AddSlider("maxRewindAge", "最远回溯（秒）", typeof(float), (float)_config.MaxRewindAge, new Vector2(10f, 120f));
            ModConfigBridge.AddSlider("timelineStep", "时间轴间隔（秒）", typeof(float), (float)_config.TimelineStep, new Vector2(1f, 60f));
            ModConfigBridge.AddSlider("sampleInterval", "采样间隔（秒）", typeof(float), (float)_config.SampleInterval, new Vector2(0.05f, 1f));
            ModConfigBridge.AddBool("autoRewind", "死亡后自动回溯（无人值守测试）", _config.AutoRewind);
            ModConfigBridge.AddBool("debugMode", "调试模式", _config.DebugMode);
            ModConfigBridge.AddBool("logVerbose", "详细日志", _config.LogVerbose);
            ModConfigBridge.AddOptionsChanged(OnModConfigOptionChanged);
        }

        private void OnModConfigOptionChanged(string key)
        {
            if (key == null || !key.StartsWith(ModConfigBridge.Prefixed(""))) return;
            _config.ReloadFromModConfig();
            _ring?.Reconfigure(_config.AnchorCount, _config.MaxRewindAge, _config.MaxRewinds, _config.AnchorInterval);
            ModLog.Log($"[DeathRewind] 游戏内设置变更生效: {key} " +
                      $"(interval={_config.AnchorInterval:0.#}s count={_config.AnchorCount} maxAge={_config.MaxRewindAge:0.#}s step={_config.TimelineStep:0.#}s scope={_config.RewindScope})");
        }

        protected override void OnBeforeDeactivate()
        {
            ReleaseTimeScale();
            DeathInterceptor.Uninstall();
            EnemyPersistencePatch.Uninstall();
            RewindIndicator.Dispose();
            _hooks?.Uninstall();
            ModLog.Log("[DeathRewind] deactivated");
        }

        /// <summary>
        /// 锚点缩略图：在锚点构建的**同一帧**直接抓屏。
        /// 与锚点状态（0.25s 采样的玩家位姿）的时间差 ≤ 一帧，保证"图示位置 = 回溯位置"；
        /// 早期用独立 5s 循环缓存画面，与锚点网格不同步，最多漂移一个锚点间隔。
        /// CaptureScreenshotAsTexture 读的是上一渲染帧（≈30ms 前），成本数毫秒、每 5s 一次可接受。
        /// </summary>
        private byte[] CaptureAnchorThumbnail() => ThumbnailCapture.Capture();

        private void Update()
        {
            if (_config == null || !_config.Enabled) return;
            _clock.Tick(Time.unscaledTime);
            _ring.SetTick(_clock.CurrentTick);

            string sceneKey = LevelAccess.CurrentSceneKey;
            if (LevelAccess.MultiSceneLoading)
                _startAnchorNotBeforeTick = Math.Max(_startAnchorNotBeforeTick, _clock.CurrentTick + LoadingSettleSeconds);

            // 冻结期间：不采样、不推进网格（规格 §6.2/§6.3/§7 步骤 3）
            if (!_clock.IsFrozen)
            {
                if (_clock.CurrentTick >= _nextSampleAt)
                {
                    _nextSampleAt = _clock.CurrentTick + _config.SampleInterval;
                    _sampler.Sample(_clock.CurrentTick);
                }

                var due = _ring.PollDueGridTimestamp();
                bool settled = _clock.CurrentTick >= _startAnchorNotBeforeTick;
                // 基地（Base）是安全屋，没有回溯场景——不采锚点，省内存/IO
                bool inBase = LevelAccess.InBaseLevel;
                // 允许任何稳定停留的场景建锚点（楼层、主层等 additive 游玩层）；
                // 地道/室内短暂切换靠 MultiSceneLoading 推未来时间 + StartAnchorSettleSeconds 自然过滤
                if (!inBase && due.HasValue && LevelAccess.LevelReady && settled)
                {
                    _pendingBuild = _builder.BeginBuild(_ring.AllocateId(), _clock.CurrentTick, due.Value);
                }
                else if (_pendingBuild == null && !inBase && LevelAccess.LevelReady && settled && !_scenesWithStartAnchor.Contains(sceneKey))
                {
                    // 当前场景尚无起点锚点时补建：该场景内不足回溯窗口长度时，回退目标就是它
                    _pendingBuild = _builder.BeginBuild(_ring.AllocateId(), _clock.CurrentTick, _levelStartTick ?? _clock.CurrentTick);
                }
            }

            // active scene 切换：清理进行中的锚点构建，并推进静置窗口避免在过渡帧采集
            if (_lastSceneKey == null)
            {
                _lastSceneKey = sceneKey;
            }
            else if (_lastSceneKey != sceneKey)
            {
                _lastSceneKey = sceneKey;
                _pendingBuild = null;
                // 新场景需要重新静置，MultiSceneLoading 仍会继续推这个时间戳
                _startAnchorNotBeforeTick = Math.Max(_startAnchorNotBeforeTick, _clock.CurrentTick + StartAnchorSettleSeconds);
                // 首次稳定下来的 active scene 记为"主要游玩场景"（只作日志标识，后续楼层/地道切换不再重设）
                if (_mapSceneKey == null) _mapSceneKey = sceneKey;
            }

            // 推进正在进行的锚点构建：采集分帧进行，文件 IO 在后台线程
            if (_pendingBuild != null)
            {
                if (!_pendingBuild.CaptureDone)
                {
                    if (_builder.ContinueCapture(_pendingBuild)) _builder.StartIo(_pendingBuild);
                }
                else if (_builder.IsIoCompleted(_pendingBuild))
                {
                    var anchor = _builder.CompleteBuild(_pendingBuild);
                    _pendingBuild = null;
                    if (anchor != null)
                    {
                        var evicted = new List<Anchor>();
                        _ring.Insert(anchor, evicted);
                        foreach (var old in evicted)
                        {
                            _sampler.ReleaseAnchor(old);
                            if (old.SceneKey != null && _levelStartAnchors.TryGetValue(old.SceneKey, out var start) && old == start)
                            {
                                _levelStartAnchors.Remove(old.SceneKey);        // 起点锚点出环后不可再回溯到它
                                _scenesWithStartAnchor.Remove(old.SceneKey);     // 允许稍后回该场景时重建起点锚点
                            }
                        }
                        _sampler.SetAnchorCoverage(_ring.Anchors);
                        // 每个场景的第一个锚点 = 进入该场景那一刻
                        if (anchor.SceneKey != null && !_levelStartAnchors.ContainsKey(anchor.SceneKey))
                        {
                            _levelStartAnchors[anchor.SceneKey] = anchor;
                            _scenesWithStartAnchor.Add(anchor.SceneKey);
                        }
                        ModLog.Log($"[DeathRewind] anchor created id={anchor.Id} gridTs={anchor.GridTimestamp} absTs={anchor.AbsTimestamp}");
                    }
                }
            }

            // HUD "1up" 硬币指示：与死亡拦截同一判定（RewindCount < MaxRewinds），
            // 还有回溯机会时显示、用掉后自动隐藏；HUD 未就绪时内部带 1s 重试节流
            // 基地（Base）是安全屋没有回溯场景，不显示 1UP
            bool shouldShow = _ring != null
                && _ring.RewindCount < _config.MaxRewinds
                && !LevelAccess.InBaseLevel;
            RewindIndicator.SetAvailable(shouldShow);

            HandleDebugHotkeys();
        }

        private void HandleLevelStarted()
        {
            // 时钟不复位（有效 tick 必须单调），只重置网格原点与状态
            ModLog.Log($"[DR-Trace] HandleLevelStarted → 调用 _ring.Clear()（RewindCount 将从 {_ring.RewindCount} 归零）");
            _ring.Clear();
            _ring.ResetGridTo(_clock.CurrentTick, _clock.CurrentTick);
            _store.Clear();
            _sampler.ResetRegistry();
            _builder?.ResetCache();
            _pendingBuild = null;
            _nextSampleAt = _clock.CurrentTick;
            _levelStartTick = _clock.CurrentTick;
            _startAnchorNotBeforeTick = _clock.CurrentTick + StartAnchorSettleSeconds;
            // mapSceneKey 不再在 OnLevelInitialized 时刻钉死——该时刻 active scene 通常还是壳场景
            // （Level_JLab_Main / Level_HiddenWarehouse_Main），addtive 游玩层加载要等几百毫秒之后。
            // 留 null 让 Update 在首次 active scene 稳定时自然记录。
            _mapSceneKey = null;
            _lastSceneKey = null;
            _levelStartAnchors.Clear();
            _scenesWithStartAnchor.Clear();
            ModLog.Log($"[DeathRewind] level started: anchors/rewindCount/store reset at tick={_clock.CurrentTick}");
        }

        private void HandleSubSceneLoaded(string sceneName)
        {
            // 只刷新注册表，不清锚点环、不归零回退计数（规格 §8.2）
            _sampler.RefreshRegistry();

            // 子场景重新加载 = 全新实例：同名场景的旧锚点属于上一次访问，
            // 场景世界原点可能随传送点变化，留着会把玩家回溯到上一次访问的位置 → 丢弃
            DropSceneAnchors(sceneName);

            ModLog.Log($"[DeathRewind] subscene loaded: scene={sceneName} anchors={_ring.Anchors.Count} rewindCount={_ring.RewindCount}");
        }

        /// <summary>
        /// 丢弃某个场景的全部锚点与起点锚点（子场景重新加载时调用）。
        /// 该场景下次进入会重建自己的起点锚点，等价于"重新进图"。
        /// </summary>
        private void DropSceneAnchors(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || _ring == null) return;

            var removed = new List<Anchor>();
            int count = _ring.RemoveByScene(sceneName, removed);
            foreach (var old in removed)
            {
                _sampler.ReleaseAnchor(old);
            }

            // 起点锚点可能已不在环里（例如被回溯丢弃），一并清掉，保证下次进入重建
            if (_levelStartAnchors.Remove(sceneName)) _scenesWithStartAnchor.Remove(sceneName);

            if (count > 0)
            {
                _sampler.SetAnchorCoverage(_ring.Anchors);
                ModLog.Log($"[DeathRewind] 丢弃旧场景锚点: scene={sceneName} count={count} 剩余={_ring.Anchors.Count}");
            }
        }

        private void HandleLevelEnded()
        {
            ModLog.Log("[DR-Trace] HandleLevelEnded invoked — CLEARING RING (source unknown)");
            ReleaseTimeScale();
            _ring.Clear();
            _store.Clear();
            _sampler.DestroyAllTrackedEnemies();
            _sampler.ResetRegistry();
            AnchorBuilder.DeleteAllAnchorFiles();
            ModLog.Log("[DeathRewind] level ended: state cleared");
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
                    ModLog.Log($"[DeathRewind] debug auto death after {elapsed:0.0}s");
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
                ModLog.LogWarning($"[DeathRewind] 调试致死经 Health.Hurt 失败（{e.Message}），改用直接触发死亡事件");
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

        private void OnGUI()
        {
            // 死亡裁决面板（IMGUI 不受 timeScale=0 影响）
            DeathInterceptor.DrawPrompt();
        }

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
        public void ReleaseAnchor(Anchor anchor)
        {
            _sampler?.ReleaseAnchor(anchor);
            // 起点锚点被丢弃后不能再作为"回到进图位置"的回退目标
            if (anchor?.SceneKey != null
                && _levelStartAnchors.TryGetValue(anchor.SceneKey, out var start)
                && start == anchor)
            {
                _levelStartAnchors.Remove(anchor.SceneKey);
                _scenesWithStartAnchor.Remove(anchor.SceneKey);
            }
        }
        public void ReleaseCommittedDestroy() => _sampler?.DestroyDeferredEnemies();
    }
}
