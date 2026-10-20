using System.Threading.Tasks;
using DeathRewind.Core;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 世界状态恢复器。
    /// TODO: 后续任务实现（Task 9/10）。当前为最小桩：只记录死亡上下文，不做任何恢复。
    /// </summary>
    public sealed class WorldRestorer
    {
        private readonly Config.ModConfig _config;
        private readonly AnchorRing _ring;
        private readonly LiveStateStore _store;

        public WorldRestorer(Config.ModConfig config, AnchorRing ring, LiveStateStore store)
        {
            _config = config;
            _ring = ring;
            _store = store;
        }

        /// <summary>TODO: 后续任务实现 —— 记录拦截时刻的玩家死亡态与伤害信息。</summary>
        public void SetDeathContext(DeathSnapshot death, DamageInfo captured)
        {
        }

        /// <summary>TODO: 后续任务实现 —— 恢复到指定锚点；当前直接返回 false。</summary>
        public async Task<bool> RestoreAsync(Anchor anchor)
        {
            return false;
        }
    }
}
