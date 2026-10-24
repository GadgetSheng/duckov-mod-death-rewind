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
using UnityEngine;
using UnityEngine.Events;
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

        /// <summary>结算界面时间轴的候选节点上限（1~4 个，覆盖约 60s 窗口）。</summary>
        private const int MaxTimelineNodes = 4;
        private static HarmonyLibHarmony _harmony;

        /// <summary>IMGUI 降级面板是否打开（供 ModBehaviour.OnGUI 转发判断）。</summary>
        public static bool PromptOpen => RewindPrompt.IsOpen;

        public static void Install(ModBehaviour owner, Config.ModConfig config, WorldRestorer restorer)
        {
            _owner = owner; _config = config; _restorer = restorer;
            if (!HarmonyGuard.CanRegister("com.deathrewind.interceptor"))
            {
                ModLog.LogWarning("[DeathRewind] 因 Harmony 冲突，拦截功能未启用");
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
                ModLog.LogWarning("[DeathRewind] 未找到 ClosureView.ShowAndReturnTask(DamageInfo, float)，接受死亡时结算界面会出现两次");
            }

            ModLog.Log("[DeathRewind] DeathInterceptor installed (OnMainCharacterDie prefix + ClosureView suppress)");
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
            ModLog.Log("[DeathRewind] 已跳过重放流程中的第二次死亡结算界面");
            return false;
        }

        /// <summary>
        /// 结算界面的回溯候选节点（最多 <see cref="MaxTimelineNodes"/> 个，旧→新）：
        /// 从最老（maxRewindAge，默认 60s）向内按 timelineStep（默认 10s）取点，
        /// 即约 60 / 50 / 40 / 30 四个选项；每个目标取年龄最接近的锚点。
        /// 一个都匹配不到（场景太新，没有够老的锚点）时回退到"进入该场景那一刻"的起点锚点。
        /// </summary>
        private static List<RewindCandidate> BuildCandidates(AnchorRing ring)
        {
            var list = new List<RewindCandidate>();
            var owner = _owner;
            if (owner == null) return list;
            string sceneKey = LevelAccess.CurrentSceneKey;

            var targets = ring.SelectByAge(sceneKey, _config.MaxRewindAge, _config.TimelineStep, MaxTimelineNodes);
            if (targets.Count > 0)
            {
                foreach (var a in targets)
                {
                    list.Add(new RewindCandidate(a, owner.CurrentTick - a.AbsTimestamp));
                }
                ModLog.Log($"[DeathRewind] 场景 {sceneKey} 回溯候选 {list.Count} 个：" +
                           string.Join(", ", list.ConvertAll(c => $"{c.AgeSeconds:0}s")) + "，默认取最近");
                return list;
            }

            var start = owner.GetLevelStartAnchor(sceneKey);
            // 刚进该场景就死（起点锚点年龄 <2s）→ 回溯等于原地复活，无意义，按无锚点处理
            if (start == null || owner.CurrentTick - start.AbsTimestamp < 2)
            {
                ModLog.Log($"[DeathRewind] 场景 {sceneKey} 内无回溯窗口内锚点且起点锚点不可用");
                return list;
            }
            list.Add(new RewindCandidate(start, owner.CurrentTick - start.AbsTimestamp));
            ModLog.Log($"[DeathRewind] 场景 {sceneKey} 内无回溯窗口内锚点，回溯目标取起点锚点 id={start.Id}");
            return list;
        }

        /// <summary>死亡入口前缀：捕获伤害、判定是否可回溯、冻结时间并驱动结算界面裁决。</summary>
        private static bool OnDeathPrefix(DamageInfo __0)
        {
            if (_config == null || !_config.Enabled) return true;

            // 基地（Base）是安全屋，没有回溯场景——不拦截死亡
            if (LevelAccess.InBaseLevel) return true;

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
            if (ring.RewindCount >= _config.MaxRewinds)
            {
                ModLog.Log("[DeathRewind] rewind limit reached");
                return true;
            }

            // 候选节点（含起点锚点回退：场景太新、窗口内没有匹配节点时回到进图那一刻）。
            // 不用 ring.CanRewind() 做门槛：它只看"窗口内有没有锚点"，进图 60s 内会误判"无合格锚点"。
            var candidates = BuildCandidates(ring);
            if (candidates.Count == 0)
            {
                ModLog.Log("[DeathRewind] no eligible anchor");
                return true;
            }
            // 默认选最老的节点（≈maxRewindAge，默认 60s）：与"回到约 60 秒前的自己"的承诺一致，
            // 时间轴上更近的 50/40/30 秒节点由玩家自行点选。
            var anchor = candidates[0].Anchor;

            _interceptionActive = true;
            _capturedDamage = __0;
            _deathSnapshot = CaptureDeathSnapshot();
            _restorer?.SetDeathContext(_deathSnapshot, _capturedDamage);
            _owner.FreezeTimeScale();
            _owner.SuppressLevelEnd = true;   // 裁决期间禁止楼层 unload / RaidEnd 清空锚点环
            _pendingAnchor = anchor;

            // 跨地图保护
            if (!string.IsNullOrEmpty(anchor.SceneKey) && anchor.SceneKey != LevelAccess.CurrentSceneKey)
            {
                ModLog.Log($"[DeathRewind] anchor scene mismatch: {anchor.SceneKey} != {LevelAccess.CurrentSceneKey}");
                _pendingAnchor = null;
                CancelPending();
                return true;
            }

            var ageSeconds = _owner.CurrentTick - anchor.AbsTimestamp;
            ModLog.Log($"[DeathRewind] death intercepted, anchor id={anchor.Id} age={ageSeconds:0}s");

            if (_config.AutoRewind)
            {
                // 无人值守验证：不弹界面，直接回退
                _owner.StartCoroutine(AutoRewindRoutine(anchor));
            }
            else
            {
                _rewindClicked = false;
                _owner.StartCoroutine(DeathChoiceRoutine(__0, anchor, ageSeconds, candidates));
            }

            return false; // 吞掉原 OnMainCharacterDie：原生死亡后果（掉落/墓碑/存档/结算）在玩家裁决前不发生
        }

        /// <summary>驱动原生 YOU DIED 结算界面并注入回溯按钮，等待玩家裁决。</summary>
        private static IEnumerator DeathChoiceRoutine(DamageInfo damage, Anchor anchor, double ageSeconds,
                                                   List<RewindCandidate> candidates)
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
                ModLog.LogWarning("[DeathRewind] 原生死亡结算界面不可用，降级为内置裁决面板");
                yield return FallbackPromptRoutine(gen, anchor, ageSeconds);
                yield break;
            }

            ModLog.Log("[DeathRewind] driving native ClosureView with injected rewind button");

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
                ModLog.LogWarning($"[DeathRewind] continueBtn 等待超时（{wait:0.0}s）仍未亮出，尝试强制激活");
            if (!continueBtn.gameObject.activeSelf) continueBtn.gameObject.SetActive(true);

            // 注入控件组：原生"继续"按钮 + 回溯按钮并排一行，白色提示文字与箭头在回溯按钮上方，
            // 锚点缩略图在按钮行下方（行合并后腾出的竖向空间全部给图片）。
            // 时间轴：1~4 个候选节点，默认选最近的一个；
            // 拖拽切换节点 → 换预览图 + 换回溯目标（selected 随选择变化）
            var selected = anchor;
            var panel = ClosurePanel.Create(view.transform, continueBtn, $"回溯 {ageSeconds:0} 秒", candidates);
            ModLog.Log($"[DeathRewind] 回溯控件注入: {(panel != null ? "ok" : "failed")} " +
                      $"(view={view != null} contBtn={continueBtn != null} wait={wait:0.0}s preview={(panel != null && panel.HasPreview ? "ok" : "none")} nodes={(panel != null ? panel.CandidateCount : 0)})");
            if (panel == null)
            {
                ModLog.LogWarning("[DeathRewind] 回溯控件注入失败，只能选择继续（接受死亡）");
            }
            else
            {
                panel.RewindButton.onClick.AddListener(() => { _rewindClicked = true; });
                panel.NodeSelected += index =>
                {
                    if (index < 0 || index >= candidates.Count) return;
                    selected = candidates[index].Anchor;
                    _pendingAnchor = selected;
                    panel.SetRewindLabel($"回溯 {candidates[index].AgeSeconds:0} 秒");
                    ModLog.Log($"[DeathRewind] 时间轴选中节点 #{index} age={candidates[index].AgeSeconds:0}s anchorId={selected.Id}");
                };
                if (!panel.HasPreview)
                    ModLog.LogWarning("[DeathRewind] 锚点缩略图不可用（未抓到画面）");
            }

            // 等待玩家裁决：点回溯按钮，或点原生"继续"（showTask 完成）
            while (!_rewindClicked && showTask.Status == UniTaskStatus.Pending && _choiceGeneration == gen)
            {
                // 原生按钮可能被布局组件重排或被 Punch 动画缩放，每帧重新对齐整组控件
                panel?.Layout();
                yield return null;
            }
            if (_choiceGeneration != gen) { panel?.Dispose(); yield break; }

            panel?.Dispose();

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

            yield return RestoreRoutine(selected);
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

        private static void AcceptDeath()
        {
            ModLog.Log("[DeathRewind] player chose to accept death");
            _interceptionActive = false;
            _pendingAnchor = null;
            _owner.ReleaseTimeScale();
            _owner.SuppressLevelEnd = false;
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
                ModLog.Log($"[DeathRewind] rewind result=ok rewindCount={_owner.Ring.RewindCount} consumedAnchor={anchor.Id}");
                PostReviveFixup();
                _owner.StartCoroutine(PostReviveGuard());
            }
            else
            {
                ModLog.LogWarning($"[DeathRewind] rewind result=failed rewindCount={_owner.Ring.RewindCount}");
                ScheduleReplay();
            }

            _interceptionActive = false;
            _pendingAnchor = null;
            _owner.ReleaseTimeScale();
            _owner.SuppressLevelEnd = false;   // 恢复世界清理权限
        }

        /// <summary>
        /// 复活后的无敌守卫：回溯之后外部来源仍可能把主角置回无敌/死亡/加载态
        /// （游戏内置调试设置 OnlevelInited 会重放无敌设置、作弊热键 Ctrl+=、撤离流程等）。
        /// 15 秒实时窗口内每 0.5s 复检，发现翻回即复位并记日志；玩家真的再次死亡时立即让位。
        /// </summary>
        private static IEnumerator PostReviveGuard()
        {
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                try
                {
                    if (_interceptionActive) yield break;   // 已进入新的死亡拦截流程，让位
                    var main = LevelManager.Instance?.MainCharacter;
                    var health = main?.Health;
                    if (health == null) continue;
                    if (health.IsDead && health.CurrentHealth <= 0f) yield break;   // 玩家真的又死了

                    var receiver = main.mainDamageReceiver;
                    if (receiver != null && !receiver.gameObject.activeSelf)
                    {
                        receiver.gameObject.SetActive(true);
                        ModLog.LogWarning("[DeathRewind] 复活守卫: mainDamageReceiver 被停用，已重新激活");
                    }
                    if (health.Invincible)
                    {
                        ModLog.LogWarning("[DeathRewind] 复活守卫: invincible=true 来源未知 → 强制复位，若属游戏正当保护（StormShield/撤离/特殊阶段）请反馈");
                        health.SetInvincible(false);
                    }
                    if (health.IsDead)
                    {
                        Compat.PrivateStateAccessor.SetBool(health, "isDead", false);
                        ModLog.LogWarning("[DeathRewind] 复活守卫: isDead 残留（血量>0），已复位");
                    }
                    var core = MultiSceneCore.Instance;
                    if (core != null && core.IsLoading)
                    {
                        typeof(MultiSceneCore).GetField("isLoading", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                            ?.SetValue(core, false);
                        ModLog.LogWarning("[DeathRewind] 复活守卫: IsLoading 卡在 true，已复位");
                    }
                }
                catch (Exception e)
                {
                    ModLog.LogWarning($"[DeathRewind] 复活守卫异常: {e.Message}");
                }
            }
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
                        ModLog.LogWarning("[DeathRewind] MultiSceneCore.IsLoading 卡在 true，已强制复位（全场无敌的根因）");
                    }
                    else
                    {
                        ModLog.LogWarning("[DeathRewind] IsLoading=true 但未找到 isLoading 字段，无法复位（需核对字段名）");
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] IsLoading 检查失败: {e.Message}");
            }

            // 0.5) View.ActiveView 残留检查：结算界面关闭后若残留，
            //      UpdateCursor 会持续走"有界面打开"分支（隐藏游戏准星），并保持输入封锁。
            try
            {
                var av = View.ActiveView;
                if (av != null)
                {
                    ModLog.LogWarning($"[DeathRewind] View.ActiveView 残留: {av.GetType().Name}，强制关闭");
                    try { av.Close(); } catch { /* 忽略 */ }
                    // setter 是 private static：Close 未清引用时反射置空
                    typeof(View).GetProperty("ActiveView", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        ?.SetValue(null, null);
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] ActiveView 检查失败: {e.Message}");
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
                        ModLog.Log($"[DeathRewind] 已清空 InputManager 输入封锁源 x{n}");
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
                            ModLog.Log("[DeathRewind] 已恢复 InputManager.controllingCharacter");
                        }
                    }
                    imType.GetMethod("UpdateCursor", flags)?.Invoke(inst, null);
                    var cc = imType.GetField("controllingCharacter", flags)?.GetValue(inst);
                    ModLog.Log($"[DeathRewind] 输入复位完成: controllingCharacter={(cc == null ? "null" : "ok")}");
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 输入复位失败: {e.Message}");
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
                    // 死亡流程 DamageReceiver.OnDead 会停用接收器 GameObject
                    // （子弹打不中=无敌；AI 目标句柄失效=不索敌）。原生流程死人不复活，没人负责再打开。
                    var receiver = main.mainDamageReceiver;
                    if (receiver != null && !receiver.gameObject.activeSelf)
                    {
                        receiver.gameObject.SetActive(true);
                        ModLog.LogWarning("[DeathRewind] 复活终检: mainDamageReceiver 处于停用（无敌+不索敌根因），已重新激活");
                    }
                    if (wasInvincible || wasDead || wasHandled)
                        ModLog.Log($"[DeathRewind] 复活终检复位: invincible={wasInvincible} isDead={wasDead} hasHandledDeath={wasHandled}");
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 玩家状态终检失败: {e.Message}");
            }

            // 3) 血量 UI 重同步：E2 的 SetHealth 发生在 isDead 复位之前，事件驱动的头顶血条
            //    （HealthBar 只订阅 OnHealthChange/OnHurt，且死亡期间对象池会释放条目）可能
            //    丢掉这次变更，导致血条显示与实际数值脱节。这里统一补发变更事件强制重刷。
            try
            {
                var health = LevelManager.Instance?.MainCharacter?.Health;
                if (health != null)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var name in new[] { "OnMaxHealthChange", "OnHealthChange" })
                    {
                        if (typeof(Health).GetField(name, flags)?.GetValue(health) is UnityEvent<Health> ev)
                            ev.Invoke(health);
                    }
                    ModLog.Log($"[DeathRewind] 血量 UI 事件补发: hp={health.CurrentHealth:0.#}/{health.MaxHealth:0.#}");
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 血量 UI 事件补发失败: {e.Message}");
            }

            // 4) 头顶血条重建：HealthBar.DeathTask（协程）在玩家死亡时会把 HealthBar 隐藏/移入对象池。
            //    我们补发 OnHealthChange 只能刷新已有条目的填充率，若条目本身已被销毁/隐藏则无效。
            //    这里直接通过 HealthBarManager 强制重建 MainCharacter 的头顶血条。
            try
            {
                var hbmType = AccessTools.TypeByName("Duckov.UI.HealthBarManager");
                if (hbmType != null)
                {
                    var manager = UnityEngine.Object.FindObjectOfType(hbmType);
                    if (manager != null)
                    {
                        var main = LevelManager.Instance?.MainCharacter;
                        if (main != null)
                        {
                            var createFor = AccessTools.Method(hbmType, "CreateHealthBarFor");
                            if (createFor != null)
                            {
                                createFor.Invoke(manager, new object[] { main });
                                ModLog.Log("[DeathRewind] 头顶血条重建: CreateHealthBarFor(MainCharacter) 已调用");
                            }
                            else
                            {
                                ModLog.LogWarning("[DeathRewind] 头顶血条重建: 未找到 CreateHealthBarFor 方法");
                            }
                        }
                    }
                    else
                    {
                        ModLog.LogWarning("[DeathRewind] 头顶血条重建: HealthBarManager 未找到（可能还未激活）");
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 头顶血条重建失败: {e.Message}");
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
            _owner.SuppressLevelEnd = false;
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
