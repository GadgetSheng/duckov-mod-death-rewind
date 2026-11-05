using System.Collections.Generic;

namespace DeathRewind.Core
{
    /// <summary>击杀目标的积分档位。</summary>
    public enum KillCategory
    {
        Boss,
        Storm,
        Mercenary,
        Common,
    }

    /// <summary>
    /// 击杀分类与积分换算（纯逻辑）。
    ///
    /// 敌人类型识别依据游戏自身的判据：<c>CharacterMainControl.characterPreset.nameKey</c>——
    /// 原生击杀计数（<c>SavesCounter.AddKillCount</c>）与击杀任务（<c>QuestTask_KillCount</c>）
    /// 都用这个字段匹配敌人类型；nameKey 取值与本地化表的 <c>Cname_*</c> 键一致。
    /// BOSS 优先级最高（风暴 BOSS 噗咙噗咙等虽有风暴 nameKey，但 <c>isBossCharacter</c> 为 true → 按 BOSS 计）。
    /// </summary>
    public static class KillScore
    {
        /// <summary>BOSS 击杀积分。</summary>
        public const int BossPoints = 55;
        /// <summary>风暴生物击杀积分。</summary>
        public const int StormPoints = 25;
        /// <summary>雇佣兵 / 游荡者击杀积分。</summary>
        public const int MercPoints = 15;
        /// <summary>其他生物击杀积分（兜底档）。</summary>
        public const int CommonPoints = 5;
        /// <summary>带星精英（头顶五角星，characterIconType=elete）在基础档位上的额外加成。</summary>
        public const int StarBonus = 5;

        /// <summary>风暴生物：仅本体（风暴虫/风暴？/风暴机器人等衍生体按普通生物 5 分计）。</summary>
        private static readonly HashSet<string> StormKeys = new HashSet<string>
        {
            "Cname_StormCreature",
        };

        /// <summary>雇佣兵 / 游荡者族：本体 + 各自的雪地/冰原变体。</summary>
        private static readonly HashSet<string> MercKeys = new HashSet<string>
        {
            "Cname_Usec",        // 雇佣兵
            "Cname_Usec_Ice",    // 雪地雇佣兵
            "Cname_Raider",      // 游荡者
            "Cname_RaiderIce",   // 观测者（游荡者冰原变体）
        };

        /// <summary>击杀分类：BOSS 优先，其次风暴生物，再次雇佣兵/游荡者，其余兜底。</summary>
        public static KillCategory CategoryFor(string nameKey, bool isBoss)
        {
            if (isBoss) return KillCategory.Boss;
            if (nameKey != null && StormKeys.Contains(nameKey)) return KillCategory.Storm;
            if (nameKey != null && MercKeys.Contains(nameKey)) return KillCategory.Mercenary;
            return KillCategory.Common;
        }

        /// <summary>该击杀值多少分。带星精英（头顶五角星）在所属档位上额外 +<see cref="StarBonus"/>。</summary>
        public static int PointsFor(string nameKey, bool isBoss, bool isStarElite = false)
        {
            int points;
            switch (CategoryFor(nameKey, isBoss))
            {
                case KillCategory.Boss: points = BossPoints; break;
                case KillCategory.Storm: points = StormPoints; break;
                case KillCategory.Mercenary: points = MercPoints; break;
                default: points = CommonPoints; break;
            }
            return isStarElite ? points + StarBonus : points;
        }

        /// <summary>分类的中文描述（击杀日志用）。</summary>
        public static string Describe(string nameKey, bool isBoss)
        {
            switch (CategoryFor(nameKey, isBoss))
            {
                case KillCategory.Boss: return "BOSS";
                case KillCategory.Storm: return "风暴生物";
                case KillCategory.Mercenary: return "雇佣兵/游荡者";
                default: return "生物";
            }
        }
    }
}
