using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 同进程内的**重复实例仲裁**。
    ///
    /// <para>
    /// 游戏会同时加载两份 <c>DeathRewind</c>（Steam 创意工坊副本 + <c>Contents/Mods</c> 本地副本，
    /// 见 CODE_MAP §10.1）。两份是**不同的程序集实例**（不同路径 → 各自的静态状态互不相通），
    /// 于是它们会各建一套锚点环、各自抓一次缩略图（每 5 秒多渲两次场景）、
    /// 各自驱动一枚 HUD 硬币——幽灵副本的 1UP 经济从未被扣减，
    /// 它的硬币会一直盖在胜出副本的硬币上（表现为"用掉回溯机会后状态栏仍然显示 1UP"）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **本游戏的 Mod DLL 是 <c>Assembly.Load(byte[])</c> 动态加载的，Mono 的
    /// <c>AppDomain.GetAssemblies()</c> 不返回字节加载的程序集**（实测：两份副本按程序集扫描
    /// 互相看不见、双双自称 primary，双实例并行跑）——所以仲裁必须扫描**行为体实例**
    /// （<c>Resources.FindObjectsOfTypeAll</c> 跨程序集有效），判据用游戏基类
    /// <c>ModBehaviour.info</c>（<c>ModInfo.path</c> / <c>dllPath</c>）拿磁盘上的 DLL 信息。
    /// </para>
    ///
    /// <para>
    /// 仲裁规则（优先级从高到低）：
    /// ① <b>本地 <c>Contents/Mods</c> 副本优先</b> —— 创意工坊副本的写入时间是 Steam 下载时间，
    ///    随时可能比本地刚构建的副本还新；只看时间会让"改了代码没生效"变成常态（CODE_MAP §10.1 记录的老坑）。
    /// ② 同为本地或同为工坊 → <b>DLL 写入时间较新</b>的胜出。
    /// ③ 仍相同 → 按程序集 MVID 字典序（确定性，与加载顺序无关）。
    /// 胜出者在装补丁**之前**让落败的那份停用（反射调用对方实例的
    /// <c>DeathRewindStandDown()</c>；对方是旧版本没有该方法时，回退为卸载对方补丁并禁用其行为体）。
    /// 落败方自己在初始化时也会检查：若已有更优先的副本在跑，就**什么都不装**直接停用。
    /// </para>
    /// </summary>
    public static class InstanceGuard
    {
        private const string BehaviourTypeName = "DeathRewind.ModBehaviour";
        private const string StandDownMethodName = "DeathRewindStandDown";

        /// <summary>游戏自己的 Mods 目录标记（本地副本 → 优先于创意工坊副本）。</summary>
        private const string LocalModsMarker = "/Contents/Mods/";

        /// <summary>其它副本里的补丁类型（对方版本没有停用方法时，按这些类型逐个卸载）。</summary>
        private static readonly string[] PatchTypeNames =
        {
            "DeathRewind.Harmony.DeathInterceptor",
            "DeathRewind.Harmony.EnemyPersistencePatch",
            "DeathRewind.Harmony.EnemyLootboxPatch",
        };

        /// <summary>
        /// 尝试成为"唯一实例"。返回 false 表示有更优先的副本在跑 —— 调用方应立即停用自己。
        /// </summary>
        /// <param name="selfModInfo">调用方（本副本）的 <c>ModBehaviour.info</c>（基类属性，Setup 时已就绪）。</param>
        public static bool TryBecomePrimary(object selfModInfo, out string reason)
        {
            try
            {
                var others = FindOtherInstances();
                if (others.Count == 0)
                {
                    reason = "无其它副本";
                    return true;
                }

                var mine = Describe(typeof(InstanceGuard).Assembly, selfModInfo);
                string loserDetail = null;
                foreach (var other in others)
                {
                    var od = DescribeOf(other);
                    if (Compare(od, mine) > 0)   // 对方更优先 → 我是落败方
                    {
                        loserDetail = DescribeDetail(od);
                        reason = $"存在更优先的副本（{loserDetail}）";
                        return false;
                    }
                }

                // 我更优先 → 让所有其它副本停用（必须在装自己的补丁之前）
                foreach (var other in others) StandDown(other);
                reason = others.Count == 1
                    ? $"已停用 1 个重复副本（{DescribeDetail(DescribeOf(others[0]))}）"
                    : $"已停用 {others.Count} 个重复副本";
                return true;
            }
            catch (Exception e)
            {
                // 仲裁失败不能连累整个 Mod：按"继续运行"处理，但留下痕迹。
                reason = "仲裁异常: " + e.Message;
                Debug.LogError($"[DeathRewind] 重复实例仲裁异常: {e}");
                return true;
            }
        }

        /// <summary>
        /// 当前场景里除自己以外的 DeathRewind.ModBehaviour 实例（跨程序集）。
        /// **不能按程序集扫**：字节加载的程序集不在 <c>AppDomain.GetAssemblies()</c> 里（见类注释）。
        /// </summary>
        private static List<MonoBehaviour> FindOtherInstances()
        {
            var result = new List<MonoBehaviour>();
            var ours = typeof(InstanceGuard).Assembly;
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.FullName != BehaviourTypeName) continue;
                if (t.Assembly == ours) continue;
                // 只处理场景里的实例：预制体资源里的同名组件不是运行中的副本
                if (mb.gameObject == null || !mb.gameObject.scene.IsValid()) continue;
                result.Add(mb);
            }
            return result;
        }

        /// <summary>副本身份：mod 目录 + DLL 写入时间 + MVID。</summary>
        private struct CopyInfo
        {
            public string Path;      // mod 根目录（ModInfo.path）
            public string DllPath;
            public bool IsLocalMods;
            public long WriteTicks;
            public string Mvid;
        }

        /// <summary>从行为体实例提取身份（反射读游戏基类的 <c>info</c> 属性，ModInfo 是公开字段的 struct）。</summary>
        private static CopyInfo DescribeOf(MonoBehaviour mb)
        {
            return Describe(mb.GetType().Assembly, ReadModInfo(mb));
        }

        /// <summary>
        /// 读游戏基类 <c>Duckov.Modding.ModBehaviour.info</c>（public 属性，返回 ModInfo struct）。
        /// 跨程序集拿不到编译期类型 → 反射取值。
        /// </summary>
        private static object ReadModInfo(MonoBehaviour mb)
        {
            try { return mb.GetType().GetProperty("info")?.GetValue(mb); }
            catch { return null; }
        }

        private static CopyInfo Describe(Assembly asm, object modInfo)
        {
            var info = new CopyInfo { Mvid = Mvid(asm) };
            if (modInfo == null) return info;
            try
            {
                var t = modInfo.GetType();
                string path = t.GetField("path")?.GetValue(modInfo) as string;
                string name = t.GetField("name")?.GetValue(modInfo) as string;
                info.Path = path;
                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(name))
                {
                    info.DllPath = Path.Combine(path, name + ".dll");
                    info.IsLocalMods = path.IndexOf(LocalModsMarker, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (File.Exists(info.DllPath)) info.WriteTicks = File.GetLastWriteTimeUtc(info.DllPath).Ticks;
                }
            }
            catch { /* 取不到就按 MVID 排 */ }
            return info;
        }

        private static string DescribeDetail(CopyInfo info)
        {
            string where = info.IsLocalMods ? "本地" : (info.Path != null && info.Path.IndexOf("workshop", StringComparison.OrdinalIgnoreCase) >= 0 ? "创意工坊" : "未知来源");
            string when = info.WriteTicks > 0 ? new DateTime(info.WriteTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "?";
            return $"{where} {info.Path ?? "?"} (写入 {when})";
        }

        /// <summary>
        /// a 比 b "更该胜出"返回正数。优先级：本地 Mods 副本 &gt; DLL 写入时间较新 &gt; MVID 字典序。
        /// </summary>
        private static int Compare(CopyInfo a, CopyInfo b)
        {
            if (a.IsLocalMods != b.IsLocalMods) return a.IsLocalMods ? 1 : -1;
            if (a.WriteTicks != b.WriteTicks) return a.WriteTicks > b.WriteTicks ? 1 : -1;
            return string.CompareOrdinal(a.Mvid, b.Mvid);
        }

        private static string Mvid(Assembly asm)
        {
            try { return asm.ManifestModule?.ModuleVersionId.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>让另一个副本停用：优先调用对方实例的停用方法；没有则卸载对方补丁 + 禁用其行为体。</summary>
        private static void StandDown(MonoBehaviour other)
        {
            try
            {
                var t = other.GetType();
                bool asked = false;
                var m = t.GetMethod(StandDownMethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    m.Invoke(other, null);
                    asked = true;
                }

                if (!asked)
                {
                    // 对方是旧版本（没有停用方法）：至少把它的补丁卸掉，避免双份拦截 / 双份积分。
                    var asm = t.Assembly;
                    foreach (var name in PatchTypeNames)
                    {
                        var pt = asm.GetType(name, false);
                        var um = pt?.GetMethod("Uninstall", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        um?.Invoke(null, null);
                    }
                    // 旧版本没有停用方法 → 禁用行为体（掐断 Update/OnGUI）。
                    // 不用 SetActive(false)：那是游戏 ModManager 管理的对象，关掉可能影响它的 UI 状态。
                    other.enabled = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 停用重复副本失败: {e}");
            }
        }
    }
}
