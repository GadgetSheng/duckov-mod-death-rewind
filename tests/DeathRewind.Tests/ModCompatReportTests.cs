using System.Collections.Generic;
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class ModCompatReportTests
    {
        private static CompatScanInput NewInput()
        {
            return new CompatScanInput
            {
                OurHarmonyVersion = "2.4.1.0",
            };
        }

        private static bool HasKind(CompatReport report, CompatIssueKind kind)
        {
            foreach (var issue in report.Issues)
            {
                if (issue.Kind == kind) return true;
            }
            return false;
        }

        private static CompatIssue First(CompatReport report, CompatIssueKind kind)
        {
            foreach (var issue in report.Issues)
            {
                if (issue.Kind == kind) return issue;
            }
            return null;
        }

        // —— 快捷键解析 ——

        [Fact]
        public void Parse_SplitsModifiersAndKey()
        {
            var spec = HotkeySpec.Parse("Ctrl+N");
            Assert.True(spec.IsValid);
            Assert.True(spec.Ctrl);
            Assert.False(spec.Shift);
            Assert.Equal("N", spec.Key);
            Assert.Equal("Ctrl+N", spec.Display);
        }

        [Fact]
        public void Parse_SingleTokenIsKeyNotModifier()
        {
            // "Ctrl" 单独使用时是"裸 Ctrl 键"（游戏把 Ctrl 绑成蹲下），不是修饰键
            var spec = HotkeySpec.Parse("Ctrl");
            Assert.True(spec.IsValid);
            Assert.False(spec.Ctrl);
            Assert.Equal("CTRL", spec.Key);
        }

        [Fact]
        public void Parse_EmptyIsInvalid()
        {
            Assert.False(HotkeySpec.Parse(null).IsValid);
            Assert.False(HotkeySpec.Parse("   ").IsValid);
        }

        [Fact]
        public void Overlaps_RequiresSameKeyAndModifiers()
        {
            Assert.True(HotkeySpec.Parse("Ctrl+N").Overlaps(HotkeySpec.Parse("ctrl+n")));
            Assert.False(HotkeySpec.Parse("Ctrl+N").Overlaps(HotkeySpec.Parse("Ctrl+M")));
            Assert.False(HotkeySpec.Parse("Ctrl+N").Overlaps(HotkeySpec.Parse("N")));
        }

        [Fact]
        public void TriggersBareModifier_OnlyWhenModifierMatches()
        {
            Assert.True(HotkeySpec.Parse("Ctrl+N").TriggersBareModifier(HotkeySpec.Parse("Ctrl")));
            Assert.False(HotkeySpec.Parse("Shift+N").TriggersBareModifier(HotkeySpec.Parse("Ctrl")));
            Assert.False(HotkeySpec.Parse("Ctrl+N").TriggersBareModifier(HotkeySpec.Parse("C")));
        }

        // —— 重复副本 ——

        [Fact]
        public void DuplicateCopies_AreError()
        {
            var input = NewInput();
            input.DuplicateModCopies = 2;

            var report = ModCompatAnalyzer.Analyze(input);

            Assert.True(HasKind(report, CompatIssueKind.DuplicateModCopy));
            Assert.Equal(1, report.ErrorCount);
            Assert.True(report.HasProblems);
        }

        [Fact]
        public void SingleCopy_ProducesNoDuplicateIssue()
        {
            var report = ModCompatAnalyzer.Analyze(NewInput());
            Assert.False(HasKind(report, CompatIssueKind.DuplicateModCopy));
        }

        // —— Harmony 补丁重叠 ——

        [Fact]
        public void PatchOverlap_IsWarning()
        {
            var input = NewInput();
            var overlap = new PatchOverlap { Target = "LevelManager.OnMainCharacterDie(DamageInfo)" };
            overlap.OtherOwners.Add("SuperCheat（Harmony ID: com.cheat.hack）");
            input.PatchOverlaps.Add(overlap);

            var report = ModCompatAnalyzer.Analyze(input);

            var issue = First(report, CompatIssueKind.HarmonyPatchOverlap);
            Assert.NotNull(issue);
            Assert.Equal(CompatSeverity.Warning, issue.Severity);
            Assert.Contains("OnMainCharacterDie", issue.Title);
        }

        [Fact]
        public void HarmonyVersionConflict_IsWarning()
        {
            var input = NewInput();
            input.HarmonyVersions.Add("2.4.1.0");
            input.HarmonyVersions.Add("2.3.3.0");

            var report = ModCompatAnalyzer.Analyze(input);

            var issue = First(report, CompatIssueKind.HarmonyVersionConflict);
            Assert.NotNull(issue);
            Assert.Equal(CompatSeverity.Warning, issue.Severity);
            Assert.Contains("2.3.3.0", issue.Detail);
        }

        [Fact]
        public void SingleHarmonyVersion_NoConflictIssue()
        {
            var input = NewInput();
            input.HarmonyVersions.Add("2.4.1.0");

            var report = ModCompatAnalyzer.Analyze(input);

            Assert.False(HasKind(report, CompatIssueKind.HarmonyVersionConflict));
        }

        // —— 快捷键冲突 ——

        [Fact]
        public void SameHotkeyDifferentOwners_IsWarning()
        {
            var input = NewInput();
            input.HotkeyClaims.Add(new HotkeyClaim("DeathRewind", "打开设置面板", "全局", HotkeySpec.Parse("Ctrl+N")));
            input.HotkeyClaims.Add(new HotkeyClaim("别的 Mod", "打开地图", "全局", HotkeySpec.Parse("Ctrl+N")));

            var report = ModCompatAnalyzer.Analyze(input);

            var issue = First(report, CompatIssueKind.HotkeyOverlap);
            Assert.NotNull(issue);
            Assert.Equal(CompatSeverity.Warning, issue.Severity);
        }

        [Fact]
        public void SameHotkeyDifferentScope_IsNotConflict()
        {
            var input = NewInput();
            input.HotkeyClaims.Add(new HotkeyClaim("DeathRewind", "结算界面：接受死亡", "结算界面", HotkeySpec.Parse("Escape")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "关闭调试界面", "调试界面", HotkeySpec.Parse("Escape")));

            var report = ModCompatAnalyzer.Analyze(input);

            Assert.False(HasKind(report, CompatIssueKind.HotkeyOverlap));
        }

        [Fact]
        public void OtherModsConflictWithEachOther_IsNotReported()
        {
            var input = NewInput();
            input.HotkeyClaims.Add(new HotkeyClaim("Mod A", "地图", "全局", HotkeySpec.Parse("M")));
            input.HotkeyClaims.Add(new HotkeyClaim("Mod B", "地图", "全局", HotkeySpec.Parse("M")));

            var report = ModCompatAnalyzer.Analyze(input);

            Assert.False(HasKind(report, CompatIssueKind.HotkeyOverlap));
        }

        [Fact]
        public void ModifierSideEffect_IsWarning()
        {
            var input = NewInput();
            input.HotkeyClaims.Add(new HotkeyClaim("DeathRewind", "打开设置面板", "全局", HotkeySpec.Parse("Ctrl+N")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "蹲下/起身", "全局", HotkeySpec.Parse("Ctrl")));

            var report = ModCompatAnalyzer.Analyze(input);

            var issue = First(report, CompatIssueKind.HotkeyModifierSideEffect);
            Assert.NotNull(issue);
            Assert.Equal(CompatSeverity.Warning, issue.Severity);
            Assert.Contains("蹲下", issue.Detail);
        }

        [Fact]
        public void OurHotkeys_AreListedInReport()
        {
            var input = NewInput();
            input.HotkeyClaims.Add(new HotkeyClaim("DeathRewind", "打开设置面板", "全局", HotkeySpec.Parse("Ctrl+N")));
            input.HotkeyClaims.Add(new HotkeyClaim("游戏", "冲刺", "全局", HotkeySpec.Parse("E")));

            var report = ModCompatAnalyzer.Analyze(input);

            Assert.Contains("Ctrl+N", report.OurHotkeys);
            Assert.DoesNotContain("E", report.OurHotkeys);
        }

        // —— 设置来源冲突 ——

        [Fact]
        public void SettingSourceConflict_IsWarning()
        {
            var input = NewInput();
            input.SettingConflicts.Add(new SettingSourceConflict
            {
                Key = "anchorInterval",
                ConfigTextValue = "5",
                FrameworkValue = "10",
            });

            var report = ModCompatAnalyzer.Analyze(input);

            var issue = First(report, CompatIssueKind.SettingSourceConflict);
            Assert.NotNull(issue);
            Assert.Equal(CompatSeverity.Warning, issue.Severity);
            Assert.Contains("anchorInterval", issue.Title);
        }

        [Fact]
        public void SameSettingValue_ComparesByKind()
        {
            Assert.True(ModCompatAnalyzer.SameSettingValue("bool", "true", "True"));
            Assert.False(ModCompatAnalyzer.SameSettingValue("bool", "true", "false"));
            Assert.True(ModCompatAnalyzer.SameSettingValue("int", "16", "16"));
            Assert.False(ModCompatAnalyzer.SameSettingValue("int", "16", "32"));
            Assert.True(ModCompatAnalyzer.SameSettingValue("float", "5", "5.0"));
            Assert.False(ModCompatAnalyzer.SameSettingValue("float", "5", "5.5"));
            Assert.True(ModCompatAnalyzer.SameSettingValue("string", "position", "Position"));
            Assert.False(ModCompatAnalyzer.SameSettingValue("string", "position", "full"));
        }

        // —— 框架状态与概览 ——

        [Fact]
        public void FrameworkAvailable_ReportsKeyPrefix()
        {
            var input = NewInput();
            input.FrameworkAvailable = true;
            input.FrameworkKeyPrefix = "DeathRewind_";

            var report = ModCompatAnalyzer.Analyze(input);

            var issue = First(report, CompatIssueKind.FrameworkState);
            Assert.NotNull(issue);
            Assert.Equal(CompatSeverity.Info, issue.Severity);
            Assert.Contains("DeathRewind_", issue.Detail);
        }

        [Fact]
        public void NoOtherMods_ReportsCleanState()
        {
            var report = ModCompatAnalyzer.Analyze(NewInput());

            Assert.False(report.HasProblems);
            Assert.Equal(0, report.ErrorCount);
            Assert.Equal(0, report.WarningCount);
            Assert.Contains("未检测到其它 Mod", report.ToDisplayText());
        }

        [Fact]
        public void Summary_CountsProblems()
        {
            var input = NewInput();
            input.LoadedModNames.Add("SuperCheat");
            input.DuplicateModCopies = 2;

            var report = ModCompatAnalyzer.Analyze(input);

            Assert.Contains("错误 1", report.Summary);
            Assert.Contains("警告 0", report.Summary);
        }

        [Fact]
        public void NullInput_ReturnsEmptyReport()
        {
            var report = ModCompatAnalyzer.Analyze(null);
            Assert.NotNull(report);
            Assert.Empty(report.Issues);
        }
    }
}
