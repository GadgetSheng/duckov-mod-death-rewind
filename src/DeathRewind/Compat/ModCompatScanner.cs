using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using DeathRewind.Config;
using DeathRewind.Core;
using HarmonyLib;

namespace DeathRewind.Compat
{
    /// <summary>
    /// 运行时兼容性扫描：采集**真实证据**（已加载 Mod、Harmony 补丁表、快捷键占用、设置来源），
    /// 交给纯逻辑 <see cref="ModCompatAnalyzer"/> 判定，产出 <see cref="CompatReport"/>。
    ///
    /// <para>检测能力与边界：</para>
    /// <list type="bullet">
    /// <item><b>能</b>检出：同 Mod 多份副本、Harmony 补丁目标重叠、进程内多份 0Harmony、
    /// 本 Mod 快捷键与游戏自身绑定/其它声明的冲突、config.txt 与 ModConfig 框架存档不一致。</item>
    /// <item><b>不能</b>检出：其它 Mod 的快捷键（除非它们也声明出来——本作没有跨 Mod 声明机制）、
    /// 其它 Mod 加载了**另一份 0Harmony** 时的补丁（不同 PatchProcessor 互不可见）。</item>
    /// </list>
    ///
    /// <para>扫描时机：Mod 启动后跑一次（此时本 Mod 的补丁已注册），设置面板打开时按节流刷新
    /// （覆盖"别的 Mod 晚于本 Mod 加载"的场景）。</para>
    /// </summary>
    public static class ModCompatScanner
    {
        /// <summary>本 Mod 名称。</summary>
        public const string ModName = "DeathRewind";

        /// <summary>本 Mod 注册的全部 Harmony ID（用于把"自己打的补丁"从重叠里摘掉）。</summary>
        public static readonly string[] OurHarmonyIds =
        {
            "com.deathrewind.interceptor",
            "com.deathrewind.enemylootbox",
            "com.deathrewind.enemypersistence",
            "com.deathrewind.hurtgate",
        };

        /// <summary>框架接入的设置项（键名 → 类型），用于对比 config.txt 与框架存档。</summary>
        private struct BridgedOption
        {
            public string Key;
            public string Kind;   // bool / int / float / string
        }

        // 参与"config.txt vs 框架存档"比对的选项（与 ModConfig.ApplyModConfigOverrides 一致）
        private static readonly BridgedOption[] ComparedOptions =
        {
            new BridgedOption { Key = "enabled", Kind = "bool" },
            new BridgedOption { Key = "rewindScope", Kind = "string" },
            new BridgedOption { Key = "anchorInterval", Kind = "float" },
            new BridgedOption { Key = "anchorCount", Kind = "int" },
            new BridgedOption { Key = "anchorCapacity", Kind = "int" },
            new BridgedOption { Key = "maxRewindAge", Kind = "float" },
            new BridgedOption { Key = "timelineStep", Kind = "float" },
            new BridgedOption { Key = "sampleInterval", Kind = "float" },
            new BridgedOption { Key = "autoRewind", Kind = "bool" },
            new BridgedOption { Key = "debugMode", Kind = "bool" },
            new BridgedOption { Key = "logVerbose", Kind = "bool" },
            new BridgedOption { Key = "oneUpSound", Kind = "bool" },
            new BridgedOption { Key = "oneUpSoundVolume", Kind = "float" },
        };

        /// <summary>执行一次兼容性扫描。</summary>
        /// <param name="config">当前配置（可为 null——此时只检查与配置无关的部分）。</param>
        public static CompatReport Scan(ModConfig config)
        {
            var input = new CompatScanInput();
            CollectLoadedMods(input);
            CollectHarmony(input);
            CollectHarmonyVersions(input);
            CollectHotkeys(input, config);
            CollectFramework(input, config);
            return ModCompatAnalyzer.Analyze(input);
        }

        // —— 已加载 Mod ——

        private static void CollectLoadedMods(CompatScanInput input)
        {
            try
            {
                var infos = Duckov.Modding.ModManager.modInfos;
                if (infos == null) return;
                foreach (var info in infos)
                {
                    var name = info.name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, ModName, StringComparison.OrdinalIgnoreCase))
                    {
                        input.DuplicateModCopies++;
                        continue;
                    }
                    // 只统计**已激活**的 Mod：扫描到但没启用的不会与本 Mod 抢资源
                    if (IsActive(info)) input.LoadedModNames.Add(name);
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 兼容性扫描：读取已加载 Mod 失败（{e.Message}）");
            }
        }

        private static bool IsActive(Duckov.Modding.ModInfo info)
        {
            try
            {
                Duckov.Modding.ModBehaviour instance;
                return Duckov.Modding.ModManager.IsModActive(info, out instance);
            }
            catch
            {
                return true;   // 判不出来就按"在跑"处理，宁可多报
            }
        }

        // —— Harmony 补丁重叠 ——

        private static void CollectHarmony(CompatScanInput input)
        {
            try
            {
                foreach (var method in HarmonyLib.Harmony.GetAllPatchedMethods())
                {
                    if (method == null) continue;
                    var patches = HarmonyLib.Harmony.GetPatchInfo(method);
                    if (patches == null) continue;
                    bool ours = false;
                    var others = new List<string>();
                    foreach (var owner in patches.Owners)
                    {
                        if (string.IsNullOrEmpty(owner)) continue;
                        if (IsOurHarmonyId(owner)) ours = true;
                        else others.Add(AttributeOwner(owner, input.LoadedModNames));
                    }
                    if (!ours || others.Count == 0) continue;
                    var overlap = new PatchOverlap { Target = Describe(method) };
                    overlap.OtherOwners.AddRange(others);
                    input.PatchOverlaps.Add(overlap);
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 兼容性扫描：读取 Harmony 补丁表失败（{e.Message}）");
            }
        }

        private static bool IsOurHarmonyId(string id)
        {
            foreach (var ours in OurHarmonyIds)
            {
                if (string.Equals(id, ours, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把 Harmony ID 归属到具体 Mod（按名字包含关系猜；猜不到就报 ID 本身）。</summary>
        private static string AttributeOwner(string owner, List<string> modNames)
        {
            var lower = (owner ?? string.Empty).ToLowerInvariant();
            if (lower.Length == 0) return "（未知 Harmony ID）";
            foreach (var name in modNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (lower.Contains(name.ToLowerInvariant())) return $"{name}（Harmony ID: {owner}）";
            }
            return $"未知 Mod（Harmony ID: {owner}）";
        }

        private static string Describe(MethodBase method)
        {
            try
            {
                var type = method.DeclaringType != null ? method.DeclaringType.FullName : "?";
                var ps = method.GetParameters();
                var names = new string[ps.Length];
                for (int i = 0; i < ps.Length; i++) names[i] = ps[i].ParameterType.Name;
                return $"{type}.{method.Name}({string.Join(", ", names)})";
            }
            catch
            {
                return method?.Name ?? "?";
            }
        }

        // —— 0Harmony 版本 ——

        private static void CollectHarmonyVersions(CompatScanInput input)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = asm.GetName();
                    if (name.Name != "0Harmony") continue;
                    var version = name.Version?.ToString() ?? "?";
                    if (!input.HarmonyVersions.Contains(version)) input.HarmonyVersions.Add(version);
                }
                input.OurHarmonyVersion = typeof(HarmonyLib.Harmony).Assembly.GetName().Version?.ToString() ?? "?";
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 兼容性扫描：读取 0Harmony 版本失败（{e.Message}）");
            }
        }

        // —— 快捷键 ——

        private static void CollectHotkeys(CompatScanInput input, ModConfig config)
        {
            // 本 Mod 的占用
            input.HotkeyClaims.Add(new HotkeyClaim(ModName, "打开设置面板", "全局", HotkeySpec.Parse("Ctrl+N")));
            if (config != null)
            {
                input.HotkeyClaims.Add(new HotkeyClaim(ModName, "结算界面：确认回溯", "结算界面", HotkeySpec.Parse(config.RewindHotkey)));
                input.HotkeyClaims.Add(new HotkeyClaim(ModName, "结算界面：接受死亡（继续）", "结算界面", HotkeySpec.Parse(config.ContinueHotkey)));
                if (config.DebugMode)
                    input.HotkeyClaims.Add(new HotkeyClaim(ModName, "调试：触发自身死亡", "调试模式", HotkeySpec.Parse("F10")));
            }

            // 游戏自身的绑定（全部来自游戏程序集反编译核实，见 docs/CODE_MAP.md §12）
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "蹲下/起身（按下蹲下、松开起身）", "全局", HotkeySpec.Parse("Ctrl")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "蹲下/起身（C 键）", "全局", HotkeySpec.Parse("C")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "冲刺 / 爬梯 / 情境交互", "全局", HotkeySpec.Parse("E")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "攀爬上升（特定能力）", "全局", HotkeySpec.Parse("Q")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "关闭部分调试界面（GoldMiner 调试）", "调试界面", HotkeySpec.Parse("Escape")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "输入框：确认输入", "输入框", HotkeySpec.Parse("Return")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "输入框：关闭输入框", "输入框", HotkeySpec.Parse("BackQuote")));
        }

        // —— ModConfig 框架与设置来源 ——

        private static void CollectFramework(CompatScanInput input, ModConfig config)
        {
            input.FrameworkAvailable = ModConfigBridge.Available;
            input.FrameworkKeyPrefix = ModConfigBridge.Prefixed("");
            if (!input.FrameworkAvailable) return;

            var raw = ReadConfigTextValues();
            if (raw == null) return;
            foreach (var option in ComparedOptions)
            {
                if (!raw.TryGetValue(option.Key, out var textValue)) continue;   // config.txt 没写这一项 → 无从比较
                var frameworkValue = ReadFrameworkValue(option);
                if (frameworkValue == null) continue;
                if (ModCompatAnalyzer.SameSettingValue(option.Kind, textValue, frameworkValue)) continue;
                input.SettingConflicts.Add(new SettingSourceConflict
                {
                    Key = option.Key,
                    ConfigTextValue = textValue,
                    FrameworkValue = frameworkValue,
                });
            }
        }

        /// <summary>读 config.txt 的原始键值（未做任何类型转换）。</summary>
        private static Dictionary<string, string> ReadConfigTextValues()
        {
            var result = new Dictionary<string, string>();
            try
            {
                var path = ModConfig.FilePath;
                if (!File.Exists(path)) return result;
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    result[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 兼容性扫描：读取 config.txt 失败（{e.Message}）");
            }
            return result;
        }

        /// <summary>读 ModConfig 框架存档里的值（键缺失时框架会写入 fallback，因此这些键一定已存在）。</summary>
        private static string ReadFrameworkValue(BridgedOption option)
        {
            try
            {
                switch (option.Kind)
                {
                    case "bool": return ModConfigBridge.Load(option.Key, true).ToString();
                    case "int": return ModConfigBridge.Load(option.Key, 0).ToString(CultureInfo.InvariantCulture);
                    case "float": return ModConfigBridge.Load(option.Key, 0f).ToString(CultureInfo.InvariantCulture);
                    default: return ModConfigBridge.Load(option.Key, string.Empty);
                }
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 兼容性扫描：读取框架设置 {option.Key} 失败（{e.Message}）");
                return null;
            }
        }
    }
}
