using System;
using System.Collections.Generic;

namespace DeathRewind.Core
{
    /// <summary>
    /// config.txt 文本处理（纯逻辑）：就地更新 <c>key=value</c> 行，缺失时追加到末尾。
    ///
    /// 设置面板改完设置要把值写回 config.txt，但**不能**整份重写——用户在文件里写的注释和
    /// 其它键必须原样保留。这里只替换目标键那一行，其余行不动。
    /// </summary>
    public static class ConfigFileLines
    {
        /// <summary>就地更新一行 key=value；没有该键时追加。注释行与空行原样保留。</summary>
        public static void Upsert(List<string> lines, string key, string value)
        {
            if (lines == null || string.IsNullOrEmpty(key)) return;

            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i]?.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = key + "=" + value;
                return;
            }

            lines.Add(key + "=" + value);
        }
    }
}
