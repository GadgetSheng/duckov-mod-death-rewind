using System;
using System.Collections.Generic;

namespace DeathRewind.Core
{
    /// <summary>
    /// 纯逻辑锚点环：独占网格时间线，负责 Id 分配、入环/出环、目标选择、
    /// 已消耗集合与回退计数。
    ///
    /// 回溯窗口语义：**年龄 ≤ maxAge**（maxAge = 最远能回溯到多久之前，默认 60s）。
    /// 时间轴节点从最老（maxAge）向内按 step 取（默认 10s 一步，最多 4 个 → 60/50/40/30）。
    /// </summary>
    public sealed class AnchorRing
    {
        private readonly List<Anchor> _anchors = new List<Anchor>();
        private int _capacity;
        private double _maxAge;
        private int _maxRewinds;
        private double _gridInterval;

        private double _gridOrigin;      // 网格原点（网格时间戳）
        private double _tickAtOrigin;    // 原点对应的有效 tick
        private double _nextBoundary;    // 下一条网格边界（网格时间戳）
        private double _currentTick;
        private int _nextId = 1;

        public AnchorRing(int capacity, double maxAge, int maxRewinds, double gridInterval)
        {
            _capacity = capacity;
            _maxAge = maxAge;
            _maxRewinds = maxRewinds;
            _gridInterval = gridInterval;
        }

        public int RewindCount { get; private set; }
        public IReadOnlyList<Anchor> Anchors => _anchors;
        public double CurrentTick => _currentTick;
        public double GridTimestamp => _gridOrigin + (_currentTick - _tickAtOrigin);

        public void SetTick(double currentTick) => _currentTick = currentTick;

        /// <summary>
        /// 运行时更新环参数（游戏内设置变更时调用）：保留已存锚点，仅调整容量/窗口上限/网格间隔，
        /// 并把下一条网格边界重置为"当前网格时间 + 新间隔"。
        /// </summary>
        public void Reconfigure(int capacity, double maxAge, int maxRewinds, double gridInterval)
        {
            _capacity = capacity;
            _maxAge = maxAge;
            _maxRewinds = maxRewinds;
            _gridInterval = gridInterval;
            _nextBoundary = GridTimestamp + gridInterval;
        }

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

        /// <summary>
        /// 回溯窗口内的锚点（年龄 ≤ maxAge、未被消耗、场景匹配），按旧→新排序。
        /// sceneKey 非空时只考虑该场景的锚点（子场景坐标系互不通用）。
        /// </summary>
        public List<Anchor> SelectWindow(string sceneKey, double maxAge)
        {
            var list = new List<Anchor>();
            for (int i = 0; i < _anchors.Count; i++)
            {
                var a = _anchors[i];
                if (a.Consumed) continue;
                double age = CurrentTick - a.AbsTimestamp;
                if (age < 0) continue;                               // 未来锚点（异常）不参与
                if (age > maxAge) continue;                          // 超出回溯窗口上限
                if (sceneKey != null && !string.Equals(a.SceneKey, sceneKey, StringComparison.Ordinal)) continue;
                list.Add(a);
            }
            list.Sort((x, y) => x.AbsTimestamp.CompareTo(y.AbsTimestamp));   // 旧 → 新
            return list;
        }

        /// <summary>
        /// 时间轴节点选择：从最老（maxAge）向内按 step 取 maxCount 个**目标年龄**，
        /// 每个目标取"年龄最接近且落在容差内"的锚点。
        /// 例：maxAge=60、step=10、maxCount=4 → 目标年龄 60/50/40/30（秒）。
        ///
        /// 容差 = max(step/2, 网格间隔/2)：网格比 step 稀疏时也不会漏配；
        /// 场景太新（没有够老的锚点）时匹配不到目标 → 返回更少的节点（调用方回退到起点锚点）。
        /// </summary>
        public List<Anchor> SelectByAge(string sceneKey, double maxAge, double step, int maxCount)
        {
            var result = new List<Anchor>();
            if (maxCount <= 0 || step <= 0 || maxAge <= 0) return result;
            var window = SelectWindow(sceneKey, maxAge);
            if (window.Count == 0) return result;

            double tolerance = Math.Max(step * 0.5, _gridInterval * 0.5);
            var used = new HashSet<Anchor>();
            for (int n = 0; n < maxCount; n++)
            {
                double target = maxAge - n * step;
                if (target <= 0) break;
                Anchor best = null;
                double bestDelta = double.MaxValue;
                foreach (var a in window)
                {
                    if (used.Contains(a)) continue;
                    double delta = Math.Abs((CurrentTick - a.AbsTimestamp) - target);
                    if (delta > tolerance + 1e-9) continue;
                    if (delta < bestDelta - 1e-9) { bestDelta = delta; best = a; }
                }
                if (best == null) continue;
                used.Add(best);
                result.Add(best);
            }
            result.Sort((x, y) => x.AbsTimestamp.CompareTo(y.AbsTimestamp));   // 旧 → 新
            return result;
        }

        /// <summary>是否还有回溯机会：回退次数未用尽，且回溯窗口内存在可用锚点。</summary>
        public bool CanRewind() => RewindCount < _maxRewinds && SelectWindow(null, _maxAge).Count > 0;

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

        /// <summary>
        /// 移除某个场景的全部锚点：子场景重新加载时调用。
        /// 同名场景是**全新实例**（世界原点可能随传送点变化），上一轮访问留下的锚点不再有效；
        /// 留着会被当成"当前场景的合格锚点"选中，把玩家回溯到上一次访问的位置。
        /// 被移除的锚点放入 removed 返回，返回移除数量。
        /// </summary>
        public int RemoveByScene(string sceneKey, List<Anchor> removed)
        {
            if (string.IsNullOrEmpty(sceneKey)) return 0;
            int count = 0;
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_anchors[i].SceneKey, sceneKey, StringComparison.Ordinal)) continue;
                removed.Add(_anchors[i]);
                _anchors.RemoveAt(i);
                count++;
            }
            return count;
        }

        public void Clear()
        {
            _anchors.Clear();
            RewindCount = 0;
        }
    }
}
