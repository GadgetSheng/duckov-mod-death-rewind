namespace DeathRewind.Core
{
    /// <summary>
    /// 纯逻辑时钟。有效 tick（CurrentTick）在冻结期间挂起；锚点年龄由此计算。
    /// 网格时间线由 AnchorRing 独占，本类不涉及网格原点。
    /// </summary>
    public sealed class RewindClock
    {
        private double _lastRaw;

        public double CurrentTick { get; private set; }
        public double RawTime { get; private set; }
        public bool IsFrozen { get; private set; }

        public void Tick(double rawTime)
        {
            double delta = rawTime - _lastRaw;
            if (delta < 0) delta = 0;
            _lastRaw = rawTime;
            RawTime = rawTime;
            if (IsFrozen) return;
            CurrentTick += delta;
        }

        public void Freeze() => IsFrozen = true;

        public void Resume()
        {
            IsFrozen = false;
            _lastRaw = RawTime;
        }

        public double Age(double absTimestamp) => CurrentTick - absTimestamp;
    }
}
