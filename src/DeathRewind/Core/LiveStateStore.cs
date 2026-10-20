using System.Collections.Generic;

namespace DeathRewind.Core
{
    /// <summary>
    /// 实时实体状态字典。写时克隆、读时共享 —— 读接口返回的对象**只读使用**，
    /// 需要修改时先 Clone 再写回（Unity 侧一律用 Set* 写新实例）。
    /// </summary>
    public sealed class LiveStateStore
    {
        private readonly Dictionary<string, EnemyState> _enemies = new Dictionary<string, EnemyState>();
        private readonly Dictionary<string, ContainerState> _containers = new Dictionary<string, ContainerState>();
        private readonly Dictionary<string, DoorState> _doors = new Dictionary<string, DoorState>();
        private PlayerState _player;

        public void SetPlayer(PlayerState state) => _player = state?.Clone();
        public PlayerState GetPlayer() => _player;

        public void SetEnemy(string key, EnemyState state) => _enemies[key] = state?.Clone();
        public EnemyState GetEnemy(string key) => _enemies.TryGetValue(key, out var v) ? v : null;

        public void SetContainer(string key, ContainerState state) => _containers[key] = state?.Clone();
        public ContainerState GetContainer(string key) => _containers.TryGetValue(key, out var v) ? v : null;

        public void SetDoor(string key, DoorState state) => _doors[key] = state?.Clone();
        public DoorState GetDoor(string key) => _doors.TryGetValue(key, out var v) ? v : null;

        public IEnumerable<string> EnemyKeys => _enemies.Keys;
        public IEnumerable<string> ContainerKeys => _containers.Keys;
        public IEnumerable<string> DoorKeys => _doors.Keys;

        public void Clear()
        {
            _player = null;
            _enemies.Clear();
            _containers.Clear();
            _doors.Clear();
        }

        /// <summary>锚点使用的深拷贝快照。</summary>
        public LiveStateStore Snapshot()
        {
            var copy = new LiveStateStore();
            if (_player != null) copy.SetPlayer(_player);
            foreach (var kv in _enemies) copy.SetEnemy(kv.Key, kv.Value);
            foreach (var kv in _containers) copy.SetContainer(kv.Key, kv.Value);
            foreach (var kv in _doors) copy.SetDoor(kv.Key, kv.Value);
            return copy;
        }

        /// <summary>回退成功后，用锚点状态整体替换实时状态。</summary>
        public void ReplaceFrom(Anchor anchor)
        {
            Clear();
            if (anchor == null) return;
            if (anchor.Player != null) SetPlayer(anchor.Player);
            foreach (var kv in anchor.Enemies) SetEnemy(kv.Key, kv.Value);
            foreach (var kv in anchor.Containers) SetContainer(kv.Key, kv.Value);
            foreach (var kv in anchor.Doors) SetDoor(kv.Key, kv.Value);
        }
    }
}
