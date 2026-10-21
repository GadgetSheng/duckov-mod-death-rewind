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
        public double AnchorInterval = 60;
        public int AnchorCount = 4;
        public double MinRewindAge = 120;
        public int MaxRewinds = 3;
        public double SampleInterval = 0.25;
        public bool AutoRewind;
        public string RewindHotkey = "F9";
        public string ContinueHotkey = "Escape";
        public bool DebugMode;
        public string ForceFailPhase = "";
        public bool LogVerbose;

        // macOS 实测：Application.dataPath = <app>/Contents（ModManager 扫描 <app>/Contents/Mods 可证），
        // 因此 Mod 配置目录是 <app>/Contents/ModConfig/<ModName>/（与游戏现有 ModConfig 目录一致）。
        public static string FilePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "ModConfig/DeathRewind/config.txt"));

        public static ModConfig Load()
        {
            var cfg = new ModConfig();
            var path = FilePath;
            Debug.Log($"[DeathRewind] config path: {path}");
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
                cfg.MaxRewinds = GetInt(raw, "maxRewinds", cfg.MaxRewinds);
                cfg.SampleInterval = GetDouble(raw, "sampleInterval", cfg.SampleInterval);
                cfg.AutoRewind = GetBool(raw, "autoRewind", cfg.AutoRewind);
                cfg.RewindHotkey = GetString(raw, "rewindHotkey", cfg.RewindHotkey);
                cfg.ContinueHotkey = GetString(raw, "continueHotkey", cfg.ContinueHotkey);
                cfg.DebugMode = GetBool(raw, "debugMode", cfg.DebugMode);
                cfg.ForceFailPhase = GetString(raw, "forceFailPhase", cfg.ForceFailPhase);
                cfg.LogVerbose = GetBool(raw, "logVerbose", cfg.LogVerbose);
            }
            cfg.Validate();
            return cfg;
        }

        private void Validate()
        {
            if (AnchorInterval <= 0) { Debug.LogWarning("[DeathRewind] anchorInterval 非法，回落 60"); AnchorInterval = 60; }
            if (AnchorCount < 2) { Debug.LogWarning("[DeathRewind] anchorCount 非法，回落 4"); AnchorCount = 4; }
            if (MaxRewinds < 1) { Debug.LogWarning("[DeathRewind] maxRewinds 非法，回落 3"); MaxRewinds = 3; }
            if (SampleInterval <= 0) { Debug.LogWarning("[DeathRewind] sampleInterval 非法，回落 0.25"); SampleInterval = 0.25; }
            if (MinRewindAge > AnchorCount * AnchorInterval)
            {
                Debug.LogWarning($"[DeathRewind] minRewindAge({MinRewindAge}) > anchorCount*anchorInterval({AnchorCount * AnchorInterval})，回退将永远不可用，回落");
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
            Debug.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }

        private static double GetDouble(Dictionary<string, string> raw, string key, double fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            Debug.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }

        private static int GetInt(Dictionary<string, string> raw, string key, int fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            Debug.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }
    }
}
