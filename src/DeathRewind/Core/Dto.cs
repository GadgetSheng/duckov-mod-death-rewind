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
        /// <summary>角色物品（CharacterItem）上的装备/武器槽内容（锚点构建时采集）。</summary>
        public ItemSlotPayload[] Slots = Array.Empty<ItemSlotPayload>();

        public PlayerState Clone() => new PlayerState
        {
            Position = (float[])Position.Clone(),
            Rotation = (float[])Rotation.Clone(),
            Health = Health,
            Alive = Alive,
            BagPayload = BagPayload == null ? null : (byte[])BagPayload.Clone(),
            Slots = Slots == null ? Array.Empty<ItemSlotPayload>() : (ItemSlotPayload[])Slots.Clone(),
        };
    }

    /// <summary>角色物品上某个槽位的内容载荷（装备/手持武器等）。</summary>
    public sealed class ItemSlotPayload
    {
        public string Name;
        public byte[] Payload;
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
        /// <summary>敌人背包载荷（锚点构建时采集）。死亡时游戏会把物品整体搬进战利品箱，
        /// 复活后必须按此载荷重建，否则得到空手敌人。</summary>
        public byte[] ItemPayload;
        /// <summary>敌人装备/武器槽载荷（锚点构建时采集）。</summary>
        public ItemSlotPayload[] Slots = Array.Empty<ItemSlotPayload>();

        public EnemyState Clone() => new EnemyState
        {
            Position = (float[])Position.Clone(),
            Rotation = (float[])Rotation.Clone(),
            Health = Health,
            Alive = Alive,
            AiTargetId = AiTargetId,
            Noticed = Noticed,
            ItemPayload = ItemPayload == null ? null : (byte[])ItemPayload.Clone(),
            Slots = Slots == null ? Array.Empty<ItemSlotPayload>() : (ItemSlotPayload[])Slots.Clone(),
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
        /// <summary>锚点所属地图标识：切换地图后不允许回溯到别的地图的锚点。</summary>
        public string SceneKey;
        /// <summary>锚点时刻的缩略图（PNG 字节），用于回退确认界面展示位置。</summary>
        public byte[] PreviewPng;
        public string ContainerPayloadPath;
        public string MainSaveCopyPath;
        public PlayerState Player;
        public WorldState World;
        public Dictionary<string, EnemyState> Enemies = new Dictionary<string, EnemyState>();
        public Dictionary<string, ContainerState> Containers = new Dictionary<string, ContainerState>();
        public Dictionary<string, DoorState> Doors = new Dictionary<string, DoorState>();
    }
}
