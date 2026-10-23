using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DeathRewind.Config
{
    public sealed class ModConfig
    {
        public bool Enabled = true;
        /// <summary>锚点间隔（秒）。position 模式锚点纯内存且轻量，默认 5s 一记，可回溯到约 60s 前的状态。</summary>
        public double AnchorInterval = 5;
        /// <summary>锚点环容量。默认 16 个 × 5s = 80s 覆盖窗口（minRewindAge=60 需要落在窗口内）。</summary>
        public int AnchorCount = 16;
        public double MinRewindAge = 60;
        /// <summary>设计约束：每关只能复活回溯一次。固定为 1，不可通过 config.txt / 游戏内设置修改。</summary>
        public int MaxRewinds = 1;
        public double SampleInterval = 0.25;
        public bool AutoRewind;
        public string RewindHotkey = "F9";
        public string ContinueHotkey = "Escape";
        public bool DebugMode;
        public string ForceFailPhase = "";
        public bool LogVerbose;
        /// <summary>
        /// 回溯范围：
        /// "position" = 只把玩家送回锚点位置（已获得的物品不回溯、敌人不复活）——默认；
        /// "full"     = 连同容器内容与敌人状态一起回滚（开销大、风险高）。
        /// </summary>
        public string RewindScope = "position";
        /// <summary>调试用：进入关卡 N 秒后自动触发一次自身死亡（0 = 关闭）。仅 debugMode 生效。</summary>
        public double DebugAutoDeathAfter = 0;
        /// <summary>锚点采集时每帧最多处理多少个容器（分帧摊开，避免单帧长耗时）。</summary>
        public int AnchorCaptureBatchSize = 2;
        /// <summary>锚点采集的单帧时间预算（毫秒），超出即停到下一帧。</summary>
        public double AnchorCaptureBudgetMs = 2;

        // macOS 实测：Application.dataPath = <app>/Contents（ModManager 扫描 <app>/Contents/Mods 可证），
        // 因此 Mod 配置目录是 <app>/Contents/ModConfig/<ModName>/（与游戏现有 ModConfig 目录一致）。
        public static string FilePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "ModConfig/DeathRewind/config.txt"));

        public static ModConfig Load()
        {
            var cfg = new ModConfig();
            var path = FilePath;
            ModLog.Log($"[DeathRewind] config path: {path}");
            if (File.Exists(path))
            {
                var raw = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    raw[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
                cfg.Enabled = GetBool(raw, "enabled", cfg.Enabled);
                cfg.AnchorInterval = GetDouble(raw, "anchorInterval", cfg.AnchorInterval);
                cfg.AnchorCount = GetInt(raw, "anchorCount", cfg.AnchorCount);
                cfg.MinRewindAge = GetDouble(raw, "minRewindAge", cfg.MinRewindAge);
                cfg.SampleInterval = GetDouble(raw, "sampleInterval", cfg.SampleInterval);
                cfg.AutoRewind = GetBool(raw, "autoRewind", cfg.AutoRewind);
                cfg.RewindHotkey = GetString(raw, "rewindHotkey", cfg.RewindHotkey);
                cfg.ContinueHotkey = GetString(raw, "continueHotkey", cfg.ContinueHotkey);
                cfg.DebugMode = GetBool(raw, "debugMode", cfg.DebugMode);
                cfg.ForceFailPhase = GetString(raw, "forceFailPhase", cfg.ForceFailPhase);
                cfg.LogVerbose = GetBool(raw, "logVerbose", cfg.LogVerbose);
                cfg.RewindScope = GetString(raw, "rewindScope", cfg.RewindScope);
                cfg.DebugAutoDeathAfter = GetDouble(raw, "debugAutoDeathAfter", cfg.DebugAutoDeathAfter);
                cfg.AnchorCaptureBatchSize = GetInt(raw, "anchorCaptureBatchSize", cfg.AnchorCaptureBatchSize);
                cfg.AnchorCaptureBudgetMs = GetDouble(raw, "anchorCaptureBudgetMs", cfg.AnchorCaptureBudgetMs);
            }
            // ModConfig 框架覆盖：游戏内设置界面保存的值优先于 config.txt（框架未装则原样通过）
            ApplyModConfigOverrides(cfg);
            cfg.Validate();
            return cfg;
        }

        /// <summary>用 ModConfig 框架（游戏内设置界面）保存的值覆盖 config.txt（框架未装则原样返回）。</summary>
        private static void ApplyModConfigOverrides(ModConfig cfg)
        {
            if (!ModConfigBridge.Available) return;
            cfg.Enabled = ModConfigBridge.Load("enabled", cfg.Enabled);
            cfg.AnchorInterval = ModConfigBridge.Load("anchorInterval", (float)cfg.AnchorInterval);
            cfg.AnchorCount = ModConfigBridge.Load("anchorCount", cfg.AnchorCount);
            cfg.MinRewindAge = ModConfigBridge.Load("minRewindAge", (float)cfg.MinRewindAge);
            cfg.SampleInterval = ModConfigBridge.Load("sampleInterval", (float)cfg.SampleInterval);
            cfg.RewindScope = ModConfigBridge.Load("rewindScope", cfg.RewindScope);
            cfg.AutoRewind = ModConfigBridge.Load("autoRewind", cfg.AutoRewind);
            cfg.DebugMode = ModConfigBridge.Load("debugMode", cfg.DebugMode);
            cfg.LogVerbose = ModConfigBridge.Load("logVerbose", cfg.LogVerbose);
        }

        /// <summary>游戏内设置变更后重新加载覆盖值并重新校验（ModBehaviour 的选项变更回调调用）。</summary>
        public void ReloadFromModConfig()
        {
            ApplyModConfigOverrides(this);
            Validate();
        }

        private void Validate()
        {
            if (AnchorInterval <= 0) { ModLog.LogWarning("[DeathRewind] anchorInterval 非法，回落 5"); AnchorInterval = 5; }
            if (AnchorCount < 2) { ModLog.LogWarning("[DeathRewind] anchorCount 非法，回落 16"); AnchorCount = 16; }
            if (SampleInterval <= 0) { ModLog.LogWarning("[DeathRewind] sampleInterval 非法，回落 0.25"); SampleInterval = 0.25; }
            if (AnchorCaptureBatchSize < 1) { ModLog.LogWarning("[DeathRewind] anchorCaptureBatchSize 非法，回落 2"); AnchorCaptureBatchSize = 2; }
            if (AnchorCaptureBudgetMs <= 0) { ModLog.LogWarning("[DeathRewind] anchorCaptureBudgetMs 非法，回落 2"); AnchorCaptureBudgetMs = 2; }
            if (!string.Equals(RewindScope, "position", System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(RewindScope, "full", System.StringComparison.OrdinalIgnoreCase))
            {
                ModLog.LogWarning($"[DeathRewind] rewindScope 非法（{RewindScope}），回落 position");
                RewindScope = "position";
            }
            else
            {
                RewindScope = RewindScope.ToLowerInvariant();
            }
            if (MinRewindAge > AnchorCount * AnchorInterval)
            {
                ModLog.LogWarning($"[DeathRewind] minRewindAge({MinRewindAge}) > anchorCount*anchorInterval({AnchorCount * AnchorInterval})，回退将永远不可用，回落");
                MinRewindAge = AnchorCount * AnchorInterval;
            }
        }

        private static string GetString(Dictionary<string, string> raw, string key, string fallback)
            => raw.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        private static bool GetBool(Dictionary<string, string> raw, string key, bool fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (bool.TryParse(v, out var b)) return b;
            if (v == "1") return true;
            if (v == "0") return false;
            ModLog.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }

        private static double GetDouble(Dictionary<string, string> raw, string key, double fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            ModLog.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }

        private static int GetInt(Dictionary<string, string> raw, string key, int fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            ModLog.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }
    }
}
