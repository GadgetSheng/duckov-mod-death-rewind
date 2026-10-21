using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using DeathRewind.Core;
using DeathRewind.UI;
using DeathRewind.Unity;
using Duckov.UI;
using Duckov.Scenes;
using HarmonyLib;
using HarmonyLibHarmony = HarmonyLib.Harmony;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DeathRewind.Harmony
{
    /// <summary>
    /// 死亡拦截：Hook LevelManager.OnMainCharacterDie。
    /// 可回溯时吞掉原方法 → 冻结时间 → 由本 Mod 主动驱动原生死亡结算界面（ClosureView，
    /// 即 YOU DIED 页面）并克隆原生"继续"按钮注入"回溯 N 秒"按钮。
    /// 原生结算界面只在 OnMainCharacterDie → CharacterDieTask 内部被调用（已反编译确认），
    /// 吞掉入口后它不会自己出现，必须由我们打开；玩家点"继续"接受死亡时重放原
    /// OnMainCharacterDie 走完整原生流程，并用 ShowAndReturnTask 前缀跳过第二次结算界面。
    /// </summary>
    public static class DeathInterceptor
    {
        private static ModBehaviour _owner;
        private static Config.ModConfig _config;
        private static WorldRestorer _restorer;
        private static bool _interceptionActive;
        private static bool _bypassNext;
        private static bool _closurePatchInstalled;
        private static bool _suppressNextClosure;
        private static bool _rewindClicked;
        private static int _choiceGeneration;
        private static DamageInfo _capturedDamage;
        private static DeathSnapshot _deathSnapshot;
        private static Anchor _pendingAnchor;
        private static HarmonyLibHarmony _harmony;

        /// <summary>IMGUI 降级面板是否打开（供 ModBehaviour.OnGUI 转发判断）。</summary>
        public static bool PromptOpen => RewindPrompt.IsOpen;

        public static void Install(ModBehaviour owner, Config.ModConfig config, WorldRestorer restorer)
        {
            _owner = owner; _config = config; _restorer = restorer;
            if (!HarmonyGuard.CanRegister("com.deathrewind.interceptor"))
            {
                Debug.LogWarning("[DeathRewind] 因 Harmony 冲突，拦截功能未启用");
                return;
            }

            _harmony = new HarmonyLibHarmony("com.deathrewind.interceptor");

            // 1) 拦截死亡入口：OnMainCharacterDie(DamageInfo)。
            //    必须按参数类型精确匹配，避免与其它重载产生 AmbiguousMatchException。
            var dieTarget = AccessTools.Method(typeof(LevelManager), "OnMainCharacterDie", new[] { typeof(DamageInfo) });
            if (dieTarget == null)
            {
                Debug.LogError("[DeathRewind] 未找到 LevelManager.OnMainCharacterDie(DamageInfo)，拦截功能未启用");
                _harmony = null;
                return;
            }
            var prefix = AccessTools.Method(typeof(DeathInterceptor), nameof(OnDeathPrefix));
            _harmony.Patch(dieTarget, prefix: new HarmonyMethod(prefix));

            // 2) ShowAndReturnTask(DamageInfo, float) 前缀：接受死亡的重放流程里跳过第二次结算界面。
            //    同样按参数类型精确匹配（该名下有两个重载，无类型查找会 AmbiguousMatchException）。
            var showTarget = AccessTools.Method(typeof(ClosureView), "ShowAndReturnTask", new[] { typeof(DamageInfo), typeof(float) });
            if (showTarget != null)
            {
                var suppress = AccessTools.Method(typeof(DeathInterceptor), nameof(SuppressClosureOncePrefix));
                _harmony.Patch(showTarget, prefix: new HarmonyMethod(suppress));
                _closurePatchInstalled = true;
            }
            else
            {
                Debug.LogWarning("[DeathRewind] 未找到 ClosureView.ShowAndReturnTask(DamageInfo, float)，接受死亡时结算界面会出现两次");
            }

            Debug.Log("[DeathRewind] DeathInterceptor installed (OnMainCharacterDie prefix + ClosureView suppress)");
        }

        public static void Uninstall()
        {
            RewindPrompt.Close();
            _harmony?.UnpatchAll("com.deathrewind.interceptor");
            _harmony = null;
            _interceptionActive = false;
            _bypassNext = false;
            _closurePatchInstalled = false;
            _suppressNextClosure = false;
            _rewindClicked = false;
            _choiceGeneration++;
            _pendingAnchor = null;
        }

        /// <summary>ModBehaviour.OnGUI 每帧转发：绘制 IMGUI 降级面板（未打开时零开销）。</summary>
        public static void DrawPrompt() => RewindPrompt.Draw();

        /// <summary>跳过一次死亡结算界面：重放 CharacterDieTask 时让 ShowAndReturnTask 立即完成。</summary>
        private static bool SuppressClosureOncePrefix(ref UniTask __result)
        {
            if (!_suppressNextClosure) return true;
            _suppressNextClosure = false;
            __result = UniTask.CompletedTask;
            Debug.Log("[DeathRewind] 已跳过重放流程中的第二次死亡结算界面");
            return false;
        }

        /// <summary>
        /// 回溯目标选择：进图时间不足回溯窗口长度时，回退到"进图那一刻"的状态；
        /// 否则按锚点年龄规则选最新且年龄达标的锚点。
        /// </summary>
        private static Anchor SelectTargetWithLevelStartFallback(AnchorRing ring)
        {
            double levelElapsed = _owner.CurrentTick - _owner.LevelStartTick;
            if (levelElapsed < _config.MinRewindAge)
            {
                var start = _owner.LevelStartAnchor;
                if (start != null)
                {
                    Debug.Log($"[DeathRewind] 进图仅 {levelElapsed:0}s（< {_config.MinRewindAge:0}s），回溯目标取起点锚点 id={start.Id}");
                    return start;
                }
                Debug.Log($"[DeathRewind] 进图仅 {levelElapsed:0}s，但起点锚点不可用");
            }
            return ring.SelectTarget();
        }

        /// <summary>死亡入口前缀：捕获伤害、判定是否可回溯、冻结时间并驱动结算界面裁决。</summary>
        private static bool OnDeathPrefix(DamageInfo __0)
        {
            if (_config == null || !_config.Enabled) return true;

            if (_bypassNext)
            {
                _bypassNext = false;
                return true;
            }

            if (_interceptionActive)
            {
                CancelPending();
                return true;
            }

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

            var anchor = SelectTargetWithLevelStartFallback(ring);
            _pendingAnchor = anchor;
            if (anchor == null)
            {
                Debug.Log("[DeathRewind] no eligible anchor at death");
                CancelPending();
                return true;
            }

            // 跨地图保护
            if (!string.IsNullOrEmpty(anchor.SceneKey) && anchor.SceneKey != LevelAccess.CurrentSceneKey)
            {
                Debug.Log($"[DeathRewind] anchor scene mismatch: {anchor.SceneKey} != {LevelAccess.CurrentSceneKey}");
                _pendingAnchor = null;
                CancelPending();
                return true;
            }

            var ageSeconds = _owner.CurrentTick - anchor.AbsTimestamp;
            Debug.Log($"[DeathRewind] death intercepted, anchor id={anchor.Id} age={ageSeconds:0}s");

            if (_config.AutoRewind)
            {
                // 无人值守验证：不弹界面，直接回退
                _owner.StartCoroutine(AutoRewindRoutine(anchor));
            }
            else
            {
                _rewindClicked = false;
                _owner.StartCoroutine(DeathChoiceRoutine(__0, anchor, ageSeconds));
            }

            return false; // 吞掉原 OnMainCharacterDie：原生死亡后果（掉落/墓碑/存档/结算）在玩家裁决前不发生
        }

        /// <summary>驱动原生 YOU DIED 结算界面并注入回溯按钮，等待玩家裁决。</summary>
        private static IEnumerator DeathChoiceRoutine(DamageInfo damage, Anchor anchor, double ageSeconds)
        {
            int gen = _choiceGeneration;

            // 原生死亡流程在结算前有 2.5 秒定格（ignoreTimeScale），保持同样节奏
            yield return new WaitForSecondsRealtime(2.5f);
            if (_choiceGeneration != gen) yield break;

            ClosureView view = null;
            try { view = ClosureView.Instance; } catch { /* UI 未就绪 */ }
            var continueBtn = view != null
                ? AccessTools.Field(typeof(ClosureView), "continueButton")?.GetValue(view) as Button
                : null;

            if (view == null || continueBtn == null)
            {
                Debug.LogWarning("[DeathRewind] 原生死亡结算界面不可用，降级为内置裁决面板");
                yield return FallbackPromptRoutine(gen, anchor, ageSeconds);
                yield break;
            }

            Debug.Log("[DeathRewind] driving native ClosureView with injected rewind button");

            // OnMainCharacterDie 已被吞掉，由我们主动打开原生结算界面
            var showTask = ClosureView.ShowAndReturnTask(damage, 0.5f);

            // 等 EXP 动画播完、原生"继续"按钮亮出后再克隆注入（AnimateExpBar 最长约 3s，unscaled）
            float wait = 0f;
            while (!continueBtn.gameObject.activeInHierarchy && wait < 10f && _choiceGeneration == gen)
            {
                yield return null;
                wait += Time.unscaledDeltaTime;
            }
            if (_choiceGeneration != gen) yield break;
            if (!continueBtn.gameObject.activeInHierarchy)
                Debug.LogWarning($"[DeathRewind] continueBtn 等待超时（{wait:0.0}s）仍未亮出，尝试强制激活");
            if (!continueBtn.gameObject.activeSelf) continueBtn.gameObject.SetActive(true);

            var rewindBtn = CreateRewindButtonClone(continueBtn, $"回溯 {ageSeconds:0} 秒");
            Debug.Log($"[DeathRewind] 回溯按钮注入: {(rewindBtn != null ? "ok" : "failed")} (view={view != null} contBtn={continueBtn != null} wait={wait:0.0}s)");
            UnityEngine.UI.RawImage previewImage = null;
            if (rewindBtn != null)
            {
                rewindBtn.onClick.AddListener(() => { _rewindClicked = true; });
                previewImage = CreatePreviewImage(rewindBtn, anchor.PreviewPng);
                if (previewImage == null)
                    Debug.LogWarning("[DeathRewind] 锚点缩略图不可用（未抓到画面）");
            }
            else
            {
                Debug.LogWarning("[DeathRewind] 回溯按钮注入失败，只能选择继续（接受死亡）");
            }

            // 等待玩家裁决：点回溯按钮，或点原生"继续"（showTask 完成）
            while (!_rewindClicked && showTask.Status == UniTaskStatus.Pending && _choiceGeneration == gen)
                yield return null;
            if (_choiceGeneration != gen) yield break;

            if (rewindBtn != null) UnityEngine.Object.Destroy(rewindBtn.gameObject);
            if (previewImage != null) UnityEngine.Object.Destroy(previewImage.gameObject);

            if (!_rewindClicked)
            {
                AcceptDeath();
                yield break;
            }

            // 回溯：**不能**触发原生"继续"按钮——那会让 CharacterDieTask 从 await 恢复并
            // 调用 SceneLoader.LoadBaseScene 异步跳转基地（卸载当前关卡，回溯成果作废，
            // 且 NotifySaveBeforeLoadScene 会重写存档）。让 showTask 永远 Pending 即可；
            // 直接关界面并恢复世界。
            try { view.Close(); } catch { /* 忽略 */ }

            yield return RestoreRoutine(anchor);
        }

        /// <summary>原生结算界面不可用时的 IMGI 降级面板。</summary>
        private static IEnumerator FallbackPromptRoutine(int gen, Anchor anchor, double ageSeconds)
        {
            bool done = false;
            bool rewind = false;
            RewindPrompt.Open(
                "你倒下了",
                $"回退到约 {ageSeconds:0} 秒前的世界状态？剩余回退次数 {_config.MaxRewinds - _owner.Ring.RewindCount}",
                "回退",
                "接受死亡",
                ParseKey(_config.RewindHotkey),
                ParseKey(_config.ContinueHotkey),
                submit => { rewind = submit; done = true; });

            while (!done && _choiceGeneration == gen) yield return null;
            RewindPrompt.Close();
            if (_choiceGeneration != gen) yield break;

            if (rewind) yield return RestoreRoutine(anchor);
            else AcceptDeath();
        }

        /// <summary>在回溯按钮上方放一张"锚点时刻"的缩略图，让玩家看清要回到哪里。</summary>
        private static UnityEngine.UI.RawImage CreatePreviewImage(Button rewindBtn, byte[] png)
        {
            if (rewindBtn == null || png == null || png.Length == 0) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(png)) { UnityEngine.Object.Destroy(tex); return null; }

                var go = new GameObject("DeathRewind_Preview", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
                var btnRect = rewindBtn.GetComponent<RectTransform>();
                go.transform.SetParent(btnRect != null ? btnRect.parent : rewindBtn.transform, false);
                var img = go.GetComponent<UnityEngine.UI.RawImage>();
                img.texture = tex;
                img.raycastTarget = false;

                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(320f, 180f);
                if (btnRect != null)
                    rect.anchoredPosition = btnRect.anchoredPosition + new Vector2(0f, 120f);
                return img;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 缩略图注入失败: {e.Message}");
                return null;
            }
        }

        /// <summary>克隆原生"继续"按钮作为回溯按钮（继承原生样式），返回其 Button 组件。</summary>
        private static Button CreateRewindButtonClone(Button continueBtn, string label)
        {
            try
            {
                var cloneObj = UnityEngine.Object.Instantiate(continueBtn.gameObject, continueBtn.transform.parent);
                cloneObj.name = "DeathRewind_RewindButton";

                var btn = cloneObj.GetComponent<Button>();
                if (btn == null)
                {
                    UnityEngine.Object.Destroy(cloneObj);
                    return null;
                }
                btn.onClick.RemoveAllListeners();

                var tmp = cloneObj.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null) tmp.text = label;

                // 往下挪避开原生"继续"按钮（父节点若有布局组件会自动重排）
                var rect = cloneObj.GetComponent<RectTransform>();
                if (rect != null) rect.anchoredPosition -= new Vector2(0f, rect.sizeDelta.y + 24f);

                return btn;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 创建回溯按钮失败: {e}");
                return null;
            }
        }

        private static void AcceptDeath()
        {
            Debug.Log("[DeathRewind] player chose to accept death");
            _interceptionActive = false;
            _pendingAnchor = null;
            _owner.ReleaseTimeScale();
            if (_closurePatchInstalled) _suppressNextClosure = true; // 重放时跳过第二次结算界面
            ScheduleReplay();
        }

        private static IEnumerator AutoRewindRoutine(Anchor anchor)
        {
            // 稍等片刻让死亡瞬间定格完成，再无人值守地直接回退
            yield return new WaitForSecondsRealtime(0.5f);
            yield return RestoreRoutine(anchor);
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
                try { ok = task.Result; }
                catch (Exception e) { Debug.LogError($"[DeathRewind] 回退异常: {e}"); ok = false; }
            }

            if (ok)
            {
                var removed = new List<Anchor>();
                _owner.Ring.CommitRewind(anchor, removed);
                _owner.Ring.ResetGridTo(anchor.GridTimestamp, _owner.CurrentTick);
                foreach (var old in removed) _owner.ReleaseAnchor(old);
                _owner.ReleaseCommittedDestroy();
                Debug.Log($"[DeathRewind] rewind result=ok rewindCount={_owner.Ring.RewindCount} consumedAnchor={anchor.Id}");
                PostReviveFixup();
            }
            else
            {
                Debug.LogWarning($"[DeathRewind] rewind result=failed rewindCount={_owner.Ring.RewindCount}");
                ScheduleReplay();
            }

            _interceptionActive = false;
            _pendingAnchor = null;
            _owner.ReleaseTimeScale();
        }

        private static void ScheduleReplay()
        {
            _bypassNext = true;
            var target = AccessTools.Method(typeof(LevelManager), "OnMainCharacterDie", new[] { typeof(DamageInfo) });
            var damage = _capturedDamage;
            _owner.StartCoroutine(ReplayNextFrame(target, damage));
        }

        /// <summary>
        /// 回溯成功后的"复活状态修复"：
        /// 1) InputManager：死亡/结算期间 View.OnOpen 会 DisableInput(viewGO)，回溯路径不走原生收尾，
        ///    配对的 ActiveInput 不会发生 → 封锁残留导致 InputActived 永远为 false，
        ///    表现为鼠标准星消失、无法瞄准开枪。这里清空封锁表并强制刷新光标。
        /// 2) 玩家终检：isDead / invincible / hasHandledDeath / 角色模型可见性强制复位并记日志，
        ///    任一残留都会让玩家"打不掉血（无敌）"或渲染异常。
        /// </summary>
        private static void PostReviveFixup()
        {
            // 0) MultiSceneCore.IsLoading 终检：Health.Hurt 的第一道门就是
            //    "if (MultiSceneCore.Instance.IsLoading) return false"（比 invincible/isDead 还靠前）。
            //    若该标志卡在 true（此前版本回溯触发 LoadBaseScene 后被冻结所致），全场实体免疫伤害=无敌。
            try
            {
                var core = MultiSceneCore.Instance;
                if (core != null && core.IsLoading)
                {
                    var f = typeof(MultiSceneCore).GetField("isLoading", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null)
                    {
                        f.SetValue(core, false);
                        Debug.LogWarning("[DeathRewind] MultiSceneCore.IsLoading 卡在 true，已强制复位（全场无敌的根因）");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] IsLoading 检查失败: {e.Message}");
            }

            // 0.5) View.ActiveView 残留检查：结算界面关闭后若残留，
            //      UpdateCursor 会持续走"有界面打开"分支（隐藏游戏准星），并保持输入封锁。
            try
            {
                var av = View.ActiveView;
                if (av != null)
                {
                    Debug.LogWarning($"[DeathRewind] View.ActiveView 残留: {av.GetType().Name}，强制关闭");
                    try { av.Close(); } catch { /* 忽略 */ }
                    // setter 是 private static：Close 未清引用时反射置空
                    typeof(View).GetProperty("ActiveView", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        ?.SetValue(null, null);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ActiveView 检查失败: {e.Message}");
            }

            // 1) 输入封锁复位
            try
            {
                var imType = typeof(InputManager);
                var inst = imType.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                    ?? imType.GetProperty("instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
                if (inst != null)
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    if (imType.GetField("blockInputSources", flags)?.GetValue(inst) is HashSet<GameObject> blocked && blocked.Count > 0)
                    {
                        int n = blocked.Count;
                        blocked.Clear();
                        Debug.Log($"[DeathRewind] 已清空 InputManager 输入封锁源 x{n}");
                    }
                    imType.GetField("inputActiveCoolCounter", flags)?.SetValue(inst, 0);
                    // UpdateCursor 首分支（controllingCharacter 为 null/失活）会直接隐藏自绘准星；
                    // 死亡流程可能置空它，回溯后指回玩家
                    var ccField = imType.GetField("controllingCharacter", flags);
                    if (ccField != null && ccField.GetValue(inst) == null)
                    {
                        var mainForInput = LevelManager.Instance?.MainCharacter;
                        if (mainForInput != null)
                        {
                            ccField.SetValue(inst, mainForInput);
                            Debug.Log("[DeathRewind] 已恢复 InputManager.controllingCharacter");
                        }
                    }
                    imType.GetMethod("UpdateCursor", flags)?.Invoke(inst, null);
                    var cc = imType.GetField("controllingCharacter", flags)?.GetValue(inst);
                    Debug.Log($"[DeathRewind] 输入复位完成: controllingCharacter={(cc == null ? "null" : "ok")}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 输入复位失败: {e.Message}");
            }

            // 2) 玩家状态终检（诊断 + 保险）
            try
            {
                var main = LevelManager.Instance?.MainCharacter;
                var health = main?.Health;
                if (main != null && health != null)
                {
                    bool wasInvincible = health.Invincible;
                    bool wasDead = health.IsDead;
                    bool wasHandled = Compat.PrivateStateAccessor.GetBool(main, "hasHandledDeath");
                    if (wasInvincible) health.SetInvincible(false);
                    if (wasDead) Compat.PrivateStateAccessor.SetBool(health, "isDead", false);
                    if (wasHandled) Compat.PrivateStateAccessor.SetBool(main, "hasHandledDeath", false);
                    if (!main.gameObject.activeSelf) main.gameObject.SetActive(true);
                    // 死亡流程可能隐藏角色模型（模型子物体上挂着碰撞体，隐藏=子弹打不中=无敌）
                    try
                    {
                        var model = main.characterModel;
                        if (model != null && !model.gameObject.activeSelf) model.gameObject.SetActive(true);
                    }
                    catch { /* characterModel 不可访问则跳过 */ }
                    if (wasInvincible || wasDead || wasHandled)
                        Debug.Log($"[DeathRewind] 复活终检复位: invincible={wasInvincible} isDead={wasDead} hasHandledDeath={wasHandled}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 玩家状态终检失败: {e.Message}");
            }
        }

        private static IEnumerator ReplayNextFrame(MethodInfo target, DamageInfo damage)
        {
            yield return null;
            if (target == null) yield break;
            try { target.Invoke(LevelManager.Instance, new object[] { damage }); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 重放死亡流程失败: {e}"); }
        }

        private static void CancelPending()
        {
            RewindPrompt.Close();
            _choiceGeneration++;   // 使仍在等待的裁决协程失效
            _owner?.ReleaseTimeScale();
            _interceptionActive = false;
            _pendingAnchor = null;
        }

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

        private static KeyCode ParseKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }
            catch { return KeyCode.None; }
        }
    }
}
