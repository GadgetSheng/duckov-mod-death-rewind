using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeathRewind.Core
{
    /// <summary>兼容性结论的严重级别。</summary>
    public enum CompatSeverity
    {
        /// <summary>仅供知悉，不影响功能。</summary>
        Info = 0,
        /// <summary>可能互相影响，但功能仍可用。</summary>
        Warning = 1,
        /// <summary>明确冲突，功能会异常。</summary>
        Error = 2,
    }

    /// <summary>兼容性结论的类别。</summary>
    public enum CompatIssueKind
    {
        /// <summary>已加载 Mod 概览。</summary>
        LoadedMods,
        /// <summary>同一个 Mod 加载了多份副本。</summary>
        DuplicateModCopy,
        /// <summary>Harmony 补丁目标与其它 Mod 重叠。</summary>
        HarmonyPatchOverlap,
        /// <summary>进程内存在多份不同版本的 0Harmony。</summary>
        HarmonyVersionConflict,
        /// <summary>快捷键与其它方完全重叠。</summary>
        HotkeyOverlap,
        /// <summary>快捷键的修饰键会顺带触发别的绑定（如 Ctrl 蹲下）。</summary>
        HotkeyModifierSideEffect,
        /// <summary>config.txt 与 ModConfig 框架存档不一致。</summary>
        SettingSourceConflict,
        /// <summary>ModConfig 框架接入状态。</summary>
        FrameworkState,
    }

    /// <summary>一条兼容性结论。</summary>
    public sealed class CompatIssue
    {
        public CompatSeverity Severity { get; }
        public CompatIssueKind Kind { get; }
        public string Title { get; }
        public string Detail { get; }

        public CompatIssue(CompatSeverity severity, CompatIssueKind kind, string title, string detail)
        {
            Severity = severity;
            Kind = kind;
            Title = title ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public override string ToString() => $"[{Severity}] {Title} — {Detail}";
    }

    /// <summary>
    /// 快捷键规格：修饰键 + 主键。
    ///
    /// <para>
    /// 解析规则：以 <c>+</c> 分隔，**最后一段是主键**，其余段是修饰键（Ctrl / Shift / Alt）；
    /// 只有一段时整段都是主键——于是 <c>"Ctrl"</c> 解析成"裸 Ctrl 键"（游戏把 Ctrl 单独绑定成蹲下），
    /// <c>"Ctrl+N"</c> 解析成"Ctrl + N"。主键名沿用 Unity <c>KeyCode</c> 枚举名（F9 / Escape / N…）。
    /// </para>
    /// </summary>
    public sealed class HotkeySpec
    {
        /// <summary>原始文本（可能为空或非法）。</summary>
        public string Raw { get; }
        /// <summary>规范化主键（大写）；非法输入时为 null。</summary>
        public string Key { get; }
        public bool Ctrl { get; }
        public bool Shift { get; }
        public bool Alt { get; }
        /// <summary>是否解析出了有效主键。</summary>
        public bool IsValid => !string.IsNullOrEmpty(Key);

        private HotkeySpec(string raw, string key, bool ctrl, bool shift, bool alt)
        {
            Raw = raw ?? string.Empty;
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        /// <summary>解析快捷键文本；空串/无法解析时返回无效规格（<see cref="IsValid"/> 为 false）。</summary>
        public static HotkeySpec Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new HotkeySpec(raw, null, false, false, false);
            var parts = raw.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            bool ctrl = false, shift = false, alt = false;
            string key = null;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length == 0) continue;
                bool isLast = i == parts.Length - 1;
                if (!isLast)
                {
                    switch (p.ToUpperInvariant())
                    {
                        case "CTRL":
                        case "CONTROL": ctrl = true; break;
                        case "SHIFT": shift = true; break;
                        case "ALT": alt = true; break;
                        default: key = key ?? p.ToUpperInvariant(); break;   // 未知修饰词当作主键
                    }
                    continue;
                }
                key = p.ToUpperInvariant();
            }
            if (string.IsNullOrEmpty(key)) return new HotkeySpec(raw, null, ctrl, shift, alt);
            return new HotkeySpec(raw, key, ctrl, shift, alt);
        }

        /// <summary>两组合键是否完全相同（主键一致 + 修饰键集合一致）→ 同时按下时双方都会响应。</summary>
        public bool Overlaps(HotkeySpec other)
        {
            if (other == null || !IsValid || !other.IsValid) return false;
            if (!string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)) return false;
            return Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;
        }

        /// <summary>
        /// 本组合键是否会**顺带**触发另一方的裸修饰键绑定：
        /// 例如本 Mod 用 <c>Ctrl+N</c>，游戏把 <c>Ctrl</c> 单独绑成蹲下 → 按下时角色会蹲一下。
        /// </summary>
        public bool TriggersBareModifier(HotkeySpec other)
        {
            if (other == null || !other.IsValid || !IsValid) return false;
            if (other.Ctrl || other.Shift || other.Alt) return false;   // 对方不是裸修饰键
            if (string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)) return false;   // 完全重叠由 Overlaps 处理
            if (other.Key == "CTRL" && Ctrl) return true;
            if (other.Key == "SHIFT" && Shift) return true;
            if (other.Key == "ALT" && Alt) return true;
            return false;
        }

        /// <summary>展示文本（"Ctrl+N" / "F9"）。</summary>
        public string Display
        {
            get
            {
                if (!IsValid) return string.IsNullOrEmpty(Raw) ? "（未设置）" : Raw;
                var sb = new StringBuilder();
                if (Ctrl) sb.Append("Ctrl+");
                if (Shift) sb.Append("Shift+");
                if (Alt) sb.Append("Alt+");
                sb.Append(Key);
                return sb.ToString();
            }
        }

        public override string ToString() => Display;
    }

    /// <summary>一条快捷键占用声明（谁、用来干什么、在什么场景下生效）。</summary>
    public sealed class HotkeyClaim
    {
        /// <summary>占用方（"DeathRewind" / "游戏" / 其它 Mod 名）。</summary>
        public string Owner { get; }
        /// <summary>用途描述。</summary>
        public string Purpose { get; }
        /// <summary>生效场景；"全局" 与任何场景都算同时生效。</summary>
        public string Scope { get; }
        public HotkeySpec Spec { get; }

        public HotkeyClaim(string owner, string purpose, string scope, HotkeySpec spec)
        {
            Owner = owner ?? string.Empty;
            Purpose = purpose ?? string.Empty;
            Scope = string.IsNullOrEmpty(scope) ? "全局" : scope;
            Spec = spec;
        }
    }

    /// <summary>一个 Harmony 补丁目标与其它 Mod 的重叠。</summary>
    public sealed class PatchOverlap
    {
        /// <summary>被双方同时打补丁的方法（可读描述）。</summary>
        public string Target { get; set; } = string.Empty;
        /// <summary>同一方法上其它 Mod 的 Harmony ID。</summary>
        public List<string> OtherOwners { get; } = new List<string>();
    }

    /// <summary>config.txt 与 ModConfig 框架存档不一致的一项设置。</summary>
    public sealed class SettingSourceConflict
    {
        public string Key { get; set; } = string.Empty;
        public string ConfigTextValue { get; set; } = string.Empty;
        public string FrameworkValue { get; set; } = string.Empty;
    }

    /// <summary>兼容性检查的输入（纯数据，便于单测）。</summary>
    public sealed class CompatScanInput
    {
        /// <summary>已激活的 Mod 名（不含本 Mod）。</summary>
        public List<string> LoadedModNames { get; } = new List<string>();
        /// <summary>Harmony 补丁目标重叠。</summary>
        public List<PatchOverlap> PatchOverlaps { get; } = new List<PatchOverlap>();
        /// <summary>进程内所有 0Harmony 的版本描述。</summary>
        public List<string> HarmonyVersions { get; } = new List<string>();
        /// <summary>本 Mod 使用的 0Harmony 版本。</summary>
        public string OurHarmonyVersion { get; set; } = string.Empty;
        /// <summary>快捷键占用声明。</summary>
        public List<HotkeyClaim> HotkeyClaims { get; } = new List<HotkeyClaim>();
        /// <summary>本 Mod 被加载的份数（&gt;1 即副本并行）。</summary>
        public int DuplicateModCopies { get; set; } = 1;
        /// <summary>ModConfig 框架是否可用。</summary>
        public bool FrameworkAvailable { get; set; }
        /// <summary>本 Mod 在框架里的选项键前缀。</summary>
        public string FrameworkKeyPrefix { get; set; } = string.Empty;
        /// <summary>设置来源冲突。</summary>
        public List<SettingSourceConflict> SettingConflicts { get; } = new List<SettingSourceConflict>();
    }

    /// <summary>兼容性检查结论（面板展示 + 日志输出）。</summary>
    public sealed class CompatReport
    {
        public List<CompatIssue> Issues { get; } = new List<CompatIssue>();
        /// <summary>本 Mod 占用的快捷键（面板展示）。</summary>
        public List<string> OurHotkeys { get; } = new List<string>();
        /// <summary>已激活的其它 Mod 名（面板展示）。</summary>
        public List<string> LoadedModNames { get; } = new List<string>();

        public int ErrorCount { get; private set; }
        public int WarningCount { get; private set; }
        public int InfoCount { get; private set; }
        /// <summary>是否有需要用户处理的冲突。</summary>
        public bool HasProblems => ErrorCount + WarningCount > 0;

        internal void Add(CompatIssue issue)
        {
            Issues.Add(issue);
            switch (issue.Severity)
            {
                case CompatSeverity.Error: ErrorCount++; break;
                case CompatSeverity.Warning: WarningCount++; break;
                default: InfoCount++; break;
            }
        }

        /// <summary>一句话摘要（写日志用）。</summary>
        public string Summary =>
            $"已加载 {LoadedModNames.Count} 个其它 Mod；问题 {ErrorCount + WarningCount} 项（错误 {ErrorCount} / 警告 {WarningCount}）";

        /// <summary>面板用的多行文本（每行一条结论）。</summary>
        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            foreach (var issue in Issues)
            {
                sb.Append(issue.Severity == CompatSeverity.Error ? "[×] "
                    : issue.Severity == CompatSeverity.Warning ? "[!] " : "[i] ");
                sb.Append(issue.Title);
                if (!string.IsNullOrEmpty(issue.Detail)) sb.Append("：").Append(issue.Detail);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 兼容性判定规则（纯逻辑，可单测）。
    ///
    /// <para>判定范围：</para>
    /// <list type="bullet">
    /// <item>本 Mod 被加载多份 → 错误（副本并行，各跑一套逻辑）。</item>
    /// <item>Harmony 补丁目标与其它 Mod 重叠 → 警告（执行顺序由优先级决定，双方都可能改死亡流程）。</item>
    /// <item>进程内多份不同版本的 0Harmony → 警告（对方的补丁不在同一 PatchProcessor 内，可能漏检）。</item>
    /// <item>快捷键完全重叠 → 警告；修饰键顺带触发别的绑定 → 警告。</item>
    /// <item>config.txt 与 ModConfig 框架存档不一致 → 警告（框架值优先，改 config.txt 不生效）。</item>
    /// </list>
    /// </summary>
    public static class ModCompatAnalyzer
    {
        /// <summary>本 Mod 名称（判定"占用方是本 Mod"的依据）。</summary>
        public const string OurModName = "DeathRewind";

        public static CompatReport Analyze(CompatScanInput input)
        {
            var report = new CompatReport();
            if (input == null) return report;

            AnalyzeDuplicateCopies(input, report);
            AnalyzeLoadedMods(input, report);
            AnalyzePatchOverlaps(input, report);
            AnalyzeHarmonyVersions(input, report);
            AnalyzeHotkeys(input, report);
            AnalyzeSettings(input, report);
            AnalyzeFramework(input, report);
            return report;
        }

        private static void AnalyzeDuplicateCopies(CompatScanInput input, CompatReport report)
        {
            if (input.DuplicateModCopies <= 1) return;
            report.Add(new CompatIssue(
                CompatSeverity.Error,
                CompatIssueKind.DuplicateModCopy,
                $"检测到 {input.DuplicateModCopies} 份 {OurModName} 副本同时加载",
                "副本是彼此独立的程序集实例，各跑一套锚点环/积分/HUD 指示器（互相看不见对方的状态）。" +
                "实例仲裁会停用落败方（本地 Contents/Mods 副本优先，其次 DLL 写入时间较新）；若日志仍出现两份实例，说明停用失败。"));
        }

        private static void AnalyzeLoadedMods(CompatScanInput input, CompatReport report)
        {
            foreach (var name in input.LoadedModNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!report.LoadedModNames.Contains(name)) report.LoadedModNames.Add(name);
            }
            if (report.LoadedModNames.Count == 0)
            {
                report.Add(new CompatIssue(CompatSeverity.Info, CompatIssueKind.LoadedMods,
                    "未检测到其它 Mod", "本 Mod 独占运行，无第三方 Mod 干扰。"));
                return;
            }
            report.Add(new CompatIssue(CompatSeverity.Info, CompatIssueKind.LoadedMods,
                $"已加载 {report.LoadedModNames.Count} 个其它 Mod",
                string.Join("、", report.LoadedModNames.ToArray()) +
                "。其它 Mod 的快捷键无法自动检测——本 Mod 占用的组合键见下方「本 Mod 快捷键」。"));
        }

        private static void AnalyzePatchOverlaps(CompatScanInput input, CompatReport report)
        {
            foreach (var overlap in input.PatchOverlaps)
            {
                if (overlap == null || overlap.OtherOwners.Count == 0) continue;
                report.Add(new CompatIssue(
                    CompatSeverity.Warning,
                    CompatIssueKind.HarmonyPatchOverlap,
                    $"Harmony 补丁目标重叠：{overlap.Target}",
                    "同一方法上还挂着其它 Mod 的补丁（" + string.Join("、", overlap.OtherOwners.ToArray()) +
                    "）。前缀/后缀的执行顺序由 Harmony 优先级决定，双方都可能改变死亡拦截、敌人销毁或尸体箱时机。"));
            }
        }

        private static void AnalyzeHarmonyVersions(CompatScanInput input, CompatReport report)
        {
            if (input.HarmonyVersions.Count <= 1) return;
            report.Add(new CompatIssue(
                CompatSeverity.Warning,
                CompatIssueKind.HarmonyVersionConflict,
                $"进程内存在 {input.HarmonyVersions.Count} 个 0Harmony 实例",
                "版本：" + string.Join("、", input.HarmonyVersions.ToArray()) +
                (string.IsNullOrEmpty(input.OurHarmonyVersion) ? "。" : $"；本 Mod 使用 {input.OurHarmonyVersion}。") +
                "不同 0Harmony 实例各自维护补丁表，别人的补丁可能不在本 Mod 的可见范围内（下面的重叠检测会漏报）。"));
        }

        private static void AnalyzeHotkeys(CompatScanInput input, CompatReport report)
        {
            var claims = input.HotkeyClaims;
            for (int i = 0; i < claims.Count; i++)
            {
                var a = claims[i];
                if (a?.Spec == null || !a.Spec.IsValid) continue;
                if (a.Owner == OurModName && !report.OurHotkeys.Contains(a.Spec.Display))
                    report.OurHotkeys.Add(a.Spec.Display);

                for (int j = i + 1; j < claims.Count; j++)
                {
                    var b = claims[j];
                    if (b?.Spec == null || !b.Spec.IsValid) continue;
                    bool aIsOurs = a.Owner == OurModName;
                    bool bIsOurs = b.Owner == OurModName;
                    if (!aIsOurs && !bIsOurs) continue;   // 别的 Mod 之间互撞不归我们管
                    if (!ScopesOverlap(a.Scope, b.Scope)) continue;

                    if (a.Spec.Overlaps(b.Spec))
                    {
                        report.Add(new CompatIssue(
                            CompatSeverity.Warning,
                            CompatIssueKind.HotkeyOverlap,
                            $"快捷键冲突：{a.Spec.Display}",
                            $"{a.Owner}（{a.Purpose}，{a.Scope}）与 {b.Owner}（{b.Purpose}，{b.Scope}）占用同一组合键——按下时两边都会响应。"));
                        continue;
                    }

                    if (aIsOurs && a.Spec.TriggersBareModifier(b.Spec))
                    {
                        report.Add(new CompatIssue(
                            CompatSeverity.Warning,
                            CompatIssueKind.HotkeyModifierSideEffect,
                            $"快捷键副作用：{a.Spec.Display} 会顺带触发「{b.Spec.Display}」",
                            $"{b.Owner}把 {b.Spec.Display} 单独绑定为「{b.Purpose}」，而本 Mod 的 {a.Spec.Display}（{a.Purpose}）必须按住 {b.Spec.Display}——按下时该行为也会被触发。"));
                    }
                    else if (bIsOurs && b.Spec.TriggersBareModifier(a.Spec))
                    {
                        report.Add(new CompatIssue(
                            CompatSeverity.Warning,
                            CompatIssueKind.HotkeyModifierSideEffect,
                            $"快捷键副作用：{b.Spec.Display} 会顺带触发「{a.Spec.Display}」",
                            $"{a.Owner}把 {a.Spec.Display} 单独绑定为「{a.Purpose}」，而本 Mod 的 {b.Spec.Display}（{b.Purpose}）必须按住 {a.Spec.Display}——按下时该行为也会被触发。"));
                    }
                }
            }
        }

        private static void AnalyzeSettings(CompatScanInput input, CompatReport report)
        {
            foreach (var conflict in input.SettingConflicts)
            {
                if (conflict == null) continue;
                report.Add(new CompatIssue(
                    CompatSeverity.Warning,
                    CompatIssueKind.SettingSourceConflict,
                    $"设置来源不一致：{conflict.Key}",
                    $"config.txt = {conflict.ConfigTextValue}，ModConfig 框架存档 = {conflict.FrameworkValue}。" +
                    "框架值优先——改 config.txt 不生效，需在游戏内设置界面修改。"));
            }
        }

        private static void AnalyzeFramework(CompatScanInput input, CompatReport report)
        {
            if (input.FrameworkAvailable)
            {
                report.Add(new CompatIssue(CompatSeverity.Info, CompatIssueKind.FrameworkState,
                    "ModConfig 框架已接入",
                    $"本 Mod 的框架选项键前缀为「{input.FrameworkKeyPrefix}」，与其它 Mod 隔离（框架存档是全局 ES3 键值表，只有加前缀才不会互相覆盖）。"));
            }
            else
            {
                report.Add(new CompatIssue(CompatSeverity.Info, CompatIssueKind.FrameworkState,
                    "未检测到 ModConfig 框架",
                    "全部设置走 config.txt；游戏内设置面板（Ctrl+N）的改动同样写回 config.txt。"));
            }
        }

        /// <summary>两个生效场景是否可能同时成立（"全局" 与任何场景都算）。</summary>
        private static bool ScopesOverlap(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            return a == "全局" || b == "全局";
        }

        /// <summary>比较 config.txt 文本值与框架值的字符串形式是否等价。</summary>
        public static bool SameSettingValue(string kind, string configTextValue, string frameworkValue)
        {
            if (configTextValue == null || frameworkValue == null) return false;
            switch ((kind ?? string.Empty).ToLowerInvariant())
            {
                case "bool":
                    bool cb, fb;
                    return bool.TryParse(configTextValue.Trim(), out cb)
                        && bool.TryParse(frameworkValue.Trim(), out fb)
                        && cb == fb;
                case "int":
                    int ci, fi;
                    return int.TryParse(configTextValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ci)
                        && int.TryParse(frameworkValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fi)
                        && ci == fi;
                case "float":
                    double cd, fd;
                    return double.TryParse(configTextValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out cd)
                        && double.TryParse(frameworkValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fd)
                        && Math.Abs(cd - fd) < 0.0001;
                default:
                    return string.Equals(configTextValue.Trim(), frameworkValue.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
