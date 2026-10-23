using System;
using System.Collections.Generic;
using Duckov.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeathRewind.Unity
{
    /// <summary>关卡生命周期适配：关卡开始 / 子场景加载 / 场景切换 / 关卡结束。</summary>
    public sealed class LevelHooks
    {
        private readonly HashSet<string> _subScenes = new HashSet<string>();

        /// <summary>true 时抑制 OnLevelEnded（死亡裁决期间不允许 RaidEnd/场景 unload 清空状态）。</summary>
        public bool SuppressLevelEnd { get; set; }

        public event Action OnLevelStarted;
        public event Action<string> OnSubSceneLoaded;
        public event Action OnLevelEnded;

        public void Install()
        {
            ModLog.Log("[DR-Trace] LevelHooks.Install called");
            LevelManager.OnLevelInitialized += HandleLevelInitialized;
            MultiSceneCore.OnSubSceneLoaded += HandleSubSceneLoaded;
            SceneLoader.onStartedLoadingScene += HandleSceneLoading;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            RaidUtilities.OnRaidEnd += HandleRaidEnd;
            ModLog.Log("[DR-Trace] LevelHooks.Install done — all 5 hooks subscribed");
        }

        public void Uninstall()
        {
            LevelManager.OnLevelInitialized -= HandleLevelInitialized;
            MultiSceneCore.OnSubSceneLoaded -= HandleSubSceneLoaded;
            SceneLoader.onStartedLoadingScene -= HandleSceneLoading;
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            RaidUtilities.OnRaidEnd -= HandleRaidEnd;
        }

        private void HandleLevelInitialized()
        {
            ModLog.Log("[DR-Trace] HandleLevelInitialized");
            // 不清 _subScenes：OnLevelInitialized 会在进关卡时触发一次，
            // 此时 addtive 子场景往往已经加载过或正加载中；先清空再靠 OnSubSceneLoaded 重新登记会让
            // 楼层/地道切换时 sceneUnloaded 丢失过滤依据（见 HandleSceneUnloaded）。
            OnLevelStarted?.Invoke();
        }

        private void HandleSubSceneLoaded(MultiSceneCore core, Scene scene)
        {
            // 登记当前 addtive 场景名；绝不在此清空锚点环或回退计数
            // 场景名透传给订阅方：同名子场景重新加载 = 全新实例，旧锚点必须丢弃
            _subScenes.Add(scene.name);
            ModLog.Log($"[DR-Trace] HandleSubSceneLoaded name={scene.name}");
            OnSubSceneLoaded?.Invoke(scene.name);
        }

        private void HandleSceneLoading(SceneLoadingContext ctx)
        {
            // 场景切换流程开始；可能是换图（会跟 OnLevelInitialized / RaidEnd 收口）
            // 也可能是楼层切换（只 unload 旧楼层，不走 OnLevelInitialized，此时由 sceneUnloaded 过滤）
        }

        private void HandleSceneUnloaded(Scene scene)
        {
            ModLog.Log($"[DR-Trace] HandleSceneUnloaded name={scene.name} suppress={SuppressLevelEnd} inSubScenes={_subScenes.Contains(scene.name)}");
            if (SuppressLevelEnd) { ModLog.Log("[DR-Trace] sceneUnloaded SKIP by suppress"); return; }
            if (_subScenes.Contains(scene.name)) { ModLog.Log("[DR-Trace] sceneUnloaded SKIP by _subScenes"); return; }

            int levelCount = 0;
            var liveScenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded)
                {
                    liveScenes.Add(s.name);
                    if (s.name.StartsWith("Level_") || s.name.StartsWith("Base")) levelCount++;
                }
            }
            ModLog.Log($"[DR-Trace] sceneUnloaded loadedLevelScenesNow={levelCount} liveScenes=[{string.Join(",", liveScenes)}]");
            if (levelCount >= 1) { ModLog.Log("[DR-Trace] sceneUnloaded SKIP by levelCount>=1 (floor switch)"); return; }

            ModLog.Log("[DR-Trace] sceneUnloaded FIRING OnLevelEnded");
            OnLevelEnded?.Invoke();
        }

        private void HandleRaidEnd(RaidUtilities.RaidInfo info)
        {
            ModLog.Log($"[DR-Trace] HandleRaidEnd suppress={SuppressLevelEnd}");
            if (SuppressLevelEnd) { ModLog.Log("[DR-Trace] RaidEnd SKIP by suppress"); return; }
            ModLog.Log("[DR-Trace] RaidEnd FIRING OnLevelEnded");
            OnLevelEnded?.Invoke();
        }
    }
}
