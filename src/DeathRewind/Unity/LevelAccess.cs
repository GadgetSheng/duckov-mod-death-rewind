using System;
using System.Collections.Generic;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 关卡数据的**安全**访问入口。
    /// <para>
    /// <c>LevelManager.LootBoxInventories</c> 是静态属性，其内部会解引用 <c>LevelManager.Instance</c>；
    /// 在主菜单 / 关卡尚未初始化时 Instance 为 null，直接访问会抛 NullReferenceException。
    /// 本类统一做就绪判定与异常兜底，调用方无需再自行判断。
    /// </para>
    /// </summary>
    public static class LevelAccess
    {
        /// <summary>关卡是否已初始化（Instance 存在且 LevelInited 为真）。</summary>
        public static bool LevelReady
        {
            get
            {
                try { return LevelManager.Instance != null && LevelManager.LevelInited; }
                catch { return false; }
            }
        }

        /// <summary>战利品箱注册表；关卡未就绪时返回 null（绝不抛异常）。</summary>
        public static Dictionary<int, Inventory> LootBoxInventories
        {
            get
            {
                try
                {
                    if (LevelManager.Instance == null) return null;
                    return LevelManager.LootBoxInventories;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>主角色；关卡未就绪时返回 null。</summary>
        public static CharacterMainControl MainCharacter
        {
            get
            {
                try { return LevelManager.Instance?.MainCharacter; }
                catch { return null; }
            }
        }

        /// <summary>主摄像机（用于抓取缩略图）；取不到时返回 null。</summary>
        public static Camera MainCamera
        {
            get
            {
                try
                {
                    var lm = LevelManager.Instance;
                    if (lm == null) return null;
                    var cam = lm.GameCamera;
                    if (cam != null)
                    {
                        var c = cam.GetComponent<Camera>();
                        if (c != null) return c;
                    }
                    return Camera.main;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// 当前地图标识：切换地图后变化，用于禁止跨地图回溯。
        /// 只取关卡名（不含 activeSubSceneID —— 那是同一张图里的房间流式加载，不应清空回溯历史）。
        /// </summary>
        public static string CurrentSceneKey
        {
            get
            {
                try
                {
                    var lm = LevelManager.Instance;
                    if (lm == null) return "none";
                    // 优先用 SceneManager，LevelManager 可能没有公开 sceneName
                    var scene = SceneManager.GetActiveScene();
                    return string.IsNullOrEmpty(scene.name) ? "none" : scene.name;
                }
                catch { return "none"; }
            }
        }
    }
}
