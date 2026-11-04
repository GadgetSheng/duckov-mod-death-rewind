# 多复活 + 积分 + 设置面板 设计规格

- 日期：2026-11-04
- 版本目标：v1.0.12
- 相关规格：`docs/superpowers/specs/2026-10-20-death-rewind-design.md`
- 相关代码地图：`docs/CODE_MAP.md`

## 1. 目标

给 DeathRewind 增加「多次复活」经济系统（**整套能力默认关闭**，需在设置面板开启「增强模式」）：

1. **设置面板**：游戏内按 `Ctrl+N` 打开（避开游戏原有的任务键 `Ctrl+I`），可开启「增强模式」、配置「每关 1UP 上限（1~3）」与「复活冷却间隔（30/60/90 秒）」，并展示当前得分、1UP 存量（含删除线）与冷却倒计时。
2. **积分**：击杀 BOSS（50 分）与普通生物（5 分）累计积分；每满 100 分额外发 1 次复活机会并清零积分；积分每关重算。
3. **多次复活**：每关可持有的 1UP 上限可调（默认 1，兼容现状）；用掉一次复活后进入冷却，冷却期内不能再复活；HUD 与面板展示删除线的「×N UP」与冷却倒计时。

> **默认保持原样**：`enhancedMode=false`（默认）时，本规格里的积分、上限、冷却、回溯窗口扩展**全部不生效**——
> 行为与 v1.0.11 完全一致（每关一次复活）。面板里开启增强模式后才启用。

## 2. 规则（权威定义）

> 本节全部规则只在**增强模式开启**时生效；关闭时：每关保底 1 次复活、不计分、无冷却、
> 回溯窗口固定 `maxRewindAge`（与 v1.0.11 行为一致）。

### 2.1 1UP 经济

| 概念 | 定义 |
|---|---|
| `Cap` | 每关 1UP 上限，取值 1~3（默认 1） |
| `BaseUps` | 每关保底 1 次复活（沿用现状） |
| `UpsEarned` | 本关靠积分额外获得的 1UP 数 |
| `UpsUsed` | 本关已消耗的 1UP 数 |
| `TotalUps` | `min(Cap, BaseUps + UpsEarned)` |
| `Held` | `max(0, TotalUps - UpsUsed)` —— 当前还能复活几次 |

- 积分：`BossPoints = 50`、`CreaturePoints = 5`、`PointsPerUp = 100`。
- `AddKill(isBoss)`：加分；若 `Score ≥ 100`：
  - 未达上限 → `Score = 0`、`UpsEarned++`，返回 `true`（**发放 1UP，播放音效**）；
  - 已达上限 → `Score` 封顶为 100（面板显示「已满」），返回 `false`。
- `TryConsume()`：`Held > 0` 时 `UpsUsed++` 并返回 `true`；否则 `false`。
- `ResetLevel(cap)`：关卡开始/结束时把 `Score / UpsEarned / UpsUsed` 清零，`Cap` 取新值。

### 2.2 冷却

- 冷却时长 `Duration ∈ {30, 60, 90}` 秒（默认 60），走 Mod 的**有效 tick**（`RewindClock.CurrentTick`：不受 `timeScale` 影响，死亡裁决冻结期间暂停）。
- 复活**成功**（`RestoreAsync` 返回 ok）时才扣 1UP 并开始冷却；回退失败不扣。
- 冷却期内死亡 → 不提供回溯（走原生死亡流程），日志写明剩余秒数。

### 2.3 回溯窗口随使用次数扩展

锚点环容量不变（每空间 16 个 ≈ 80s 历史）。为了让第二次之后的回溯仍有目标：

- 有效回溯窗口 `effectiveMaxAge = maxRewindAge × (已用回溯次数 + 1)`（第 1 次 60s、第 2 次 120s、第 3 次 180s）。
- 候选节点仍按 `timelineStep` 从最老向内取 4 个；找不到时回退到该空间最老锚点（判定同样用有效窗口）。
- 不改 `CommitRewind` 的语义（回退后更年轻的锚点仍被丢弃），历史深度由锚点环保留量决定。

### 2.4 击杀判定（只算玩家击杀）

- **来源**：订阅游戏自带的**静态事件** `Health.OnDead`（`Action<Health, DamageInfo>`）——
  游戏任务计数（`Duckov.Quests.Tasks.QuestTask_KillCount.Health_OnDead`）用的就是同一个事件，
  因此判定口径与任务一致。
- **归属**：`DamageInfo.fromCharacter`（伤害来源角色）的主角色标记为真 → `killer.Health.IsMainCharacterHealth`
  （等价于任务用的 `CharacterMainControlExtensions.IsMainCharacter`）才算玩家击杀。
  环境伤害、陷阱、其它角色击杀**不计分**。
- **过滤**：只有登记在敌人注册表里的 `Health` 才算（玩家自己、可破坏物、宠物等都不在表内）。
- **去重**：按稳定 id 去重，同一敌人只计一次。
- **BOSS 判定**：登记时用 `EntitySampler.IsBossEnemy`（`CharacterMainControl.isBossCharacter`）记下。
- **退订**：`Health.OnDead` 是静态事件，Mod 停用时必须 `UnsubscribeDeathEvents()` 断开。

## 3. 架构

| 单元 | 层 | 职责 |
|---|---|---|
| `Core/ScoreState.cs` | 纯逻辑 | 得分与 1UP 记账（可单测） |
| `Core/RewindCooldown.cs` | 纯逻辑 | 冷却计时（可单测） |
| `Unity/EntitySampler.cs` | Unity | 击杀检测 → `EnemyKilled(id, isBoss)` 事件 |
| `UI/RewindSettingsPanel.cs` | UI | `Ctrl+N` 设置面板（IMGUI），得分/1UP/冷却展示 + 增强模式与两项设置 |
| `Harmony/DeathInterceptor.cs` | Harmony | 死亡入口按「有 1UP 且冷却结束」放行；回退成功后扣 1UP 并开始冷却；有效窗口扩展 |
| `Config/ModConfig.cs` | Config | `maxRewinds` / `rewindCooldown` 解析、校验、写回 config.txt |
| `ModBehaviour.cs` | 入口 | 装配 ScoreState / Cooldown / 采样器事件；HUD 指示与冷却联动；OnGUI 转发面板 |

数据流：

```text
敌人死亡 ──► EntitySampler 采样比对 ──► EnemyKilled(id, isBoss)
                                              │
                                              ▼
                                       ScoreState.AddKill
                                     （满 100 → +1UP → 播音效）
                                              │
玩家死亡 ──► DeathInterceptor.OnDeathPrefix ──► Held > 0 ? 冷却结束 ?
                                              │                    │
                                       否 → 放行原生死亡    是 → 选候选 → 裁决界面
                                                                              │
                                                            回退成功 ◄────────┘
                                                                │
                                                    ScoreState.TryConsume + Cooldown.Start
```

## 4. 设置面板

- 触发：`Ctrl+N` 切换（**避开游戏原有的任务键 `Ctrl+I`**）；面板关闭时零开销。
- 位置：屏幕右上角，`GUILayout.BeginArea` + `GUI.skin.box`，不暂停游戏。
- 内容：
  - `得分 45 / 100`（达上限时显示「已满」）。
  - 1UP 行：已用的用**删除线**逐个画出来（`×1` 一个一个），剩余的显示 `×N UP`。
  - 冷却行：`就绪` 或 `冷却 12 秒`。
  - `每关 1UP 上限`：`1 / 2 / 3` 三个按钮。
  - `复活冷却`：`30 / 60 / 90` 三个按钮。
  - `关闭 (Ctrl+N)` 按钮。
- 改设置立即生效，并**写回 config.txt**（只改 `maxRewinds` / `rewindCooldown` 两行，其余原样保留）。
- 这两项**不进 ModConfig 框架**（游戏内设置面板），避免 ES3 覆盖掉面板写入的值。

## 5. HUD 与日志

- HUD「1up」硬币指示：`Held > 0 && 冷却结束 && 非基地` 时显示；冷却期内隐藏（没有可用复活）。
- 决策级日志（`ModLog.LogImportant`）：击杀加分、发放 1UP、机会用尽、冷却中、面板改设置。

## 6. 配置

| key | 默认 | 说明 |
|---|---|---|
| `maxRewinds` | `1` | 每关 1UP 上限（1~3，越界回落 1） |
| `rewindCooldown` | `60` | 复活冷却秒数（30/60/90，非法回落 60） |

## 7. 边界与降级

- 击杀事件拿不到（敌人没被采样到就死了）→ 少计分，不影响其它功能。
- 回溯目标缺失（锚点被淘汰）→ 走原有「无合格锚点」降级，放行原生死亡。
- 冷却中死亡 → 放行原生死亡，提示剩余秒数。
- 面板写 config.txt 失败（只读目录）→ 设置仍在本局生效，日志警告。

## 8. 测试策略

- `Core/ScoreStateTests.cs`：加分、满 100 发放、上限封顶、消耗、关卡重置、上限夹取。
- `Core/RewindCooldownTests.cs`：初始就绪、启动后倒计时、到点恢复、改时长重算、重置。
- Unity / IMGUI 层不写单测（依赖引擎），靠构建 + 实机验证。

## 9. 假设（未与用户逐条确认，按最合理解释实现）

0. **增强模式默认关闭**：整套积分 / 上限 / 冷却 / 窗口扩展能力都要在设置面板里显式开启；
   关闭时行为与 v1.0.11 完全一致（默认保持原样）。
1. `maxRewinds` 默认 **1**（保持现状），要多次复活需在面板里调高。
2. 上限是「本关可持有的 1UP 总数」，保底 1 次，积分只能往上加。
3. 冷却从**复活成功**那一刻开始计时，走有效 tick。
4. 面板的删除线表示**已用掉**的 1UP，`×N UP` 表示还剩几次。
5. **只算玩家击杀**：用游戏自带的 `Health.OnDead` 事件 + `DamageInfo.fromCharacter` 判定（与任务系统同源）；环境/爆炸/其它角色击杀不计分。
6. 第二次之后的回溯用「窗口 × 使用次数」扩展目标深度，不改锚点环容量。
