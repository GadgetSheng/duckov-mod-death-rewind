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

        public event Action OnLevelStarted;
        public event Action OnSubSceneLoaded;
        public event Action OnLevelEnded;

        public void Install()
        {
            LevelManager.OnLevelInitialized += HandleLevelInitialized;
            MultiSceneCore.OnSubSceneLoaded += HandleSubSceneLoaded;
            SceneLoader.onStartedLoadingScene += HandleSceneLoading;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            RaidUtilities.OnRaidEnd += HandleRaidEnd;
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
            _subScenes.Clear();
            OnLevelStarted?.Invoke();
        }

        private void HandleSubSceneLoaded(MultiSceneCore core, Scene scene)
        {
            // 只登记子场景；绝不在此清空锚点环或回退计数
            _subScenes.Add(scene.name);
            OnSubSceneLoaded?.Invoke();
        }

        private void HandleSceneLoading(SceneLoadingContext ctx)
        {
            // 场景切换流程开始；最终由 sceneUnloaded 收口
        }

        private void HandleSceneUnloaded(Scene scene)
        {
            if (_subScenes.Contains(scene.name)) return;   // 进房间 → 不清空
            OnLevelEnded?.Invoke();
        }

        private void HandleRaidEnd(RaidUtilities.RaidInfo info) => OnLevelEnded?.Invoke();
    }
}
