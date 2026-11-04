namespace DeathRewind.Core
{
    /// <summary>
    /// 「1UP」提示音的触发判定（纯逻辑，不依赖 Unity / 游戏程序集）。
    ///
    /// 语义：**每关最多响一次**——关卡开始后第一次出现"还有回溯机会"（HUD 硬币指示显示）时响一次，
    /// 相当于经典街机"拿到一条命"的提示音；之后机会用掉、指示器隐藏都不再响；
    /// 换关卡重新武装，下一关再响一次。
    ///
    /// 之所以不直接跟着指示器的可见性边沿走：主菜单里"还有回溯机会"恒成立，
    /// 指示器一旦创建就一直是 true，用边沿判定会在进游戏前就响一次。
    /// 用关卡生命周期来武装，保证"进了关卡、拿到了机会"才响。
    /// </summary>
    public sealed class OneUpCue
    {
        private bool _levelActive;
        private bool _played;

        /// <summary>关卡开始：武装本关的一次提示音（并清掉上一关的"已响过"标记）。</summary>
        public void OnLevelStarted()
        {
            _levelActive = true;
            _played = false;
        }

        /// <summary>关卡结束：解除武装，等待下一次关卡开始。</summary>
        public void OnLevelEnded()
        {
            _levelActive = false;
            _played = false;
        }

        /// <summary>
        /// 是否应当此刻播放提示音（会推进内部状态，同一关只返回一次 true）。
        /// </summary>
        /// <param name="available">当前是否"还有回溯机会"——与 HUD 1UP 指示是同一个判定。</param>
        public bool ShouldPlay(bool available)
        {
            if (!_levelActive) return false;
            if (_played) return false;
            if (!available) return false;
            _played = true;
            return true;
        }
    }
}
