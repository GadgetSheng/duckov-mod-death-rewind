using System.Collections.Generic;

namespace DeathRewind.Core
{
    /// <summary>
    /// 纯逻辑锚点环：独占网格时间线，负责 Id 分配、入环/出环、目标选择、
    /// 已消耗集合与回退计数。
    /// </summary>
    public sealed class AnchorRing
    {
        private readonly List<Anchor> _anchors = new List<Anchor>();
        private readonly int _capacity;
        private readonly double _minAge;
        private readonly int _maxRewinds;
        private readonly double _gridInterval;

        private double _gridOrigin;      // 网格原点（网格时间戳）
        private double _tickAtOrigin;    // 原点对应的有效 tick
        private double _nextBoundary;    // 下一条网格边界（网格时间戳）
        private double _currentTick;
        private int _nextId = 1;

        public AnchorRing(int capacity, double minAge, int maxRewinds, double gridInterval)
        {
            _capacity = capacity;
            _minAge = minAge;
            _maxRewinds = maxRewinds;
            _gridInterval = gridInterval;
        }

        public int RewindCount { get; private set; }
        public IReadOnlyList<Anchor> Anchors => _anchors;
        public double CurrentTick => _currentTick;
        public double GridTimestamp => _gridOrigin + (_currentTick - _tickAtOrigin);

        public void SetTick(double currentTick) => _currentTick = currentTick;

        /// <summary>把网格时间线原点重置为给定网格时间戳（关卡开始或回退后调用）。</summary>
        public void ResetGridTo(double gridTimestamp, double currentTick)
        {
            _gridOrigin = gridTimestamp;
            _tickAtOrigin = currentTick;
            _nextBoundary = gridTimestamp + _gridInterval;   // 原点不是边界
        }

        /// <summary>跨过网格边界时返回本次应建锚点的网格时间戳，否则返回 null。</summary>
        public double? PollDueGridTimestamp()
        {
            if (_nextBoundary <= GridTimestamp + 1e-9)
            {
                double ts = _nextBoundary;
                _nextBoundary += _gridInterval;
                return ts;
            }
            return null;
        }

        /// <summary>预分配锚点 Id（构建阶段用它命名 anchor_{Id}.es3 / save_{Id}.sav）。</summary>
        public int AllocateId() => _nextId++;

        /// <summary>插入锚点；超出容量时把最老锚点放入 evicted 返回。</summary>
        public Anchor Insert(Anchor anchor, List<Anchor> evicted)
        {
            if (anchor.Id <= 0) anchor.Id = AllocateId();
            else if (anchor.Id >= _nextId) _nextId = anchor.Id + 1;
            _anchors.Add(anchor);
            while (_anchors.Count > _capacity)
            {
                evicted.Add(_anchors[0]);
                _anchors.RemoveAt(0);
            }
            return anchor;
        }

        /// <summary>年龄达标且未被用作恢复目标的锚点中，时间最新的一个。</summary>
        public Anchor SelectTarget()
        {
            Anchor best = null;
            for (int i = 0; i < _anchors.Count; i++)
            {
                var a = _anchors[i];
                if (a.Consumed) continue;
                if (CurrentTick - a.AbsTimestamp < _minAge) continue;
                if (best == null || a.AbsTimestamp > best.AbsTimestamp) best = a;
            }
            return best;
        }

        public bool CanRewind() => RewindCount < _maxRewinds && SelectTarget() != null;

        /// <summary>
        /// 回退成功：移除更年轻的锚点（被丢弃时间线）、递增计数。
        /// 目标锚点**不标记 Consumed**：回溯后世界回到锚点时刻，它仍是有效的历史状态，
        /// 保留在环里可以让下一次死亡继续回溯（否则回溯后立即死亡时无锚点可选，表现为"只能复活一次"）。
        /// </summary>
        public void CommitRewind(Anchor target, List<Anchor> removed)
        {
            if (target == null) return;
            RewindCount++;
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors[i].AbsTimestamp > target.AbsTimestamp)
                {
                    removed.Add(_anchors[i]);
                    _anchors.RemoveAt(i);
                }
            }
        }

        public void Clear()
        {
            _anchors.Clear();
            RewindCount = 0;
        }
    }
}
