using System;
using System.Collections.Generic;

namespace DeathRewind.Core
{
    public sealed class PlayerState
    {
        public float[] Position = new float[3];
        public float[] Rotation = new float[4];
        public float Health;
        public bool Alive;
        public byte[] BagPayload;

        public PlayerState Clone() => new PlayerState
        {
            Position = (float[])Position.Clone(),
            Rotation = (float[])Rotation.Clone(),
            Health = Health,
            Alive = Alive,
            BagPayload = BagPayload == null ? null : (byte[])BagPayload.Clone(),
        };
    }

    public sealed class EnemyState
    {
        public float[] Position = new float[3];
        public float[] Rotation = new float[4];
        public float Health;
        public bool Alive;
        public string AiTargetId;
        /// <summary>AI 是否已察觉目标（回退时写回，避免敌人停在"困惑/问号"状态）。</summary>
        public bool Noticed;

        public EnemyState Clone() => new EnemyState
        {
            Position = (float[])Position.Clone(),
            Rotation = (float[])Rotation.Clone(),
            Health = Health,
            Alive = Alive,
            AiTargetId = AiTargetId,
            Noticed = Noticed,
        };
    }

    public sealed class ContainerState
    {
        public string Key;
        public int Capacity;
        public int[] LockedIndexes = Array.Empty<int>();
        public byte[] ItemPayload;

        public ContainerState Clone() => new ContainerState
        {
            Key = Key,
            Capacity = Capacity,
            LockedIndexes = (int[])LockedIndexes.Clone(),
            ItemPayload = ItemPayload == null ? null : (byte[])ItemPayload.Clone(),
        };
    }

    public sealed class DoorState
    {
        public string Key;
        public bool Closed;

        public DoorState Clone() => new DoorState { Key = Key, Closed = Closed };
    }

    public sealed class WorldState
    {
        public float TimeOfDay;

        public WorldState Clone() => new WorldState { TimeOfDay = TimeOfDay };
    }

    /// <summary>拦截时刻读到的玩家死亡态；反向复位的唯一权威来源。</summary>
    public sealed class DeathSnapshot
    {
        public float Health;
        public bool IsDead;
        public bool HandledDeath;
        public bool ActiveSelf;
        public bool Invincible;
        public bool RaidDead;
        public float[] Position = new float[3];
        public float[] Rotation = new float[4];
        public byte[] BagPayload;

        public DeathSnapshot Clone() => new DeathSnapshot
        {
            Health = Health,
            IsDead = IsDead,
            HandledDeath = HandledDeath,
            ActiveSelf = ActiveSelf,
            Invincible = Invincible,
            RaidDead = RaidDead,
            Position = (float[])Position.Clone(),
            Rotation = (float[])Rotation.Clone(),
            BagPayload = BagPayload == null ? null : (byte[])BagPayload.Clone(),
        };
    }

    /// <summary>回退开始时刻读到的世界侧状态；失败补偿的权威来源。</summary>
    public sealed class PreRewindSnapshot
    {
        public WorldState World = new WorldState();
        public float[] PlayerPosition = new float[3];
        public float[] PlayerRotation = new float[4];
        public byte[] PlayerBagPayload;
        public Dictionary<string, ContainerState> Containers = new Dictionary<string, ContainerState>();
        public Dictionary<string, DoorState> Doors = new Dictionary<string, DoorState>();
        public Dictionary<string, EnemyState> Enemies = new Dictionary<string, EnemyState>();
    }

    /// <summary>一个锚点。Id 单调递增，且与 anchor_{Id}.es3 / save_{Id}.sav 对应。</summary>
    public sealed class Anchor
    {
        public int Id;
        public double AbsTimestamp;   // 有效 tick 域
        public double GridTimestamp;  // 网格时间戳
        public bool Consumed;
        public string ContainerPayloadPath;
        public string MainSaveCopyPath;
        public PlayerState Player;
        public WorldState World;
        public Dictionary<string, EnemyState> Enemies = new Dictionary<string, EnemyState>();
        public Dictionary<string, ContainerState> Containers = new Dictionary<string, ContainerState>();
        public Dictionary<string, DoorState> Doors = new Dictionary<string, DoorState>();
    }
}
