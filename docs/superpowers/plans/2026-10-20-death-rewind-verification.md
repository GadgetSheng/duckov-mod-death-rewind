# 《死亡回档》Mod 游戏内验证记录

- Mod：`DeathRewind`（`Contents/Mods/DeathRewind/`）
- 日志：`~/Library/Logs/TeamSoda/Duckov/Player.log`
- 配置：`Contents/ModConfig/DeathRewind/config.txt`
- 日期：2026-10-21

## 已验证 ✅

| # | 验证项 | 证据 | 结论 |
|---|---|---|---|
| 1 | 构建 | `dotnet build src/DeathRewind/DeathRewind.csproj -c Release` → 0 error / 0 warning | 通过 |
| 2 | 纯逻辑单测 | `dotnet test tests/DeathRewind.Tests/` → 16 passed | 通过 |
| 3 | Core 分层纯净 | `bash tools/check-core-purity.sh` → `OK: Core purity` | 通过 |
| 4 | 部署 | `bash tools/build.sh` → `Contents/Mods/DeathRewind/`（DeathRewind.dll / info.ini / preview.png / 0Harmony.dll） | 通过 |
| 5 | 游戏能发现该 Mod | Player.log：`Sorted mods:` 列表含 `DeathRewind`；`ModActive_DeathRewind: True` | 通过 |
| 6 | 加载与初始化 | `Mod Loaded: DeathRewind` → `[DeathRewind] mods root: …/Contents/Mods` → `[DeathRewind] initialized` | 通过 |
| 7 | 运行期无异常 | 重启后 Player.log 中**没有任何** `at DeathRewind…` 的异常堆栈 | 通过 |
| 8 | 关卡生命周期事件 | `[DeathRewind] level started: anchors/rewindCount/store reset at tick=…`、`[DeathRewind] subscene loaded: anchors=0 rewindCount=0`、`[DeathRewind] level ended: state cleared` | 通过 |
| 9 | 锚点生成 | `[DeathRewind] anchor created id=1 gridTs=76.79 absTs=76.80 buildMs=24`；落盘 `ModRewind/anchor_1.es3`（8.5 KB）+ `ModRewind/save_1.sav`（926 KB） | 通过 |
| 10 | 配置读取 | `[DeathRewind] config path: …/Contents/ModConfig/DeathRewind/config.txt` | 通过 |
| 11 | 锚点构建耗时（实测） | 同一局内连续 20+ 个锚点：`buildMs` 稳定在 **24–42 ms**；但**冷启动第一个锚点 2690 ms**（创建 ES3 文件 + 首次主存档落盘 + 全量容器序列化） | 尖峰已定位 |
| 12 | 降级路径（无合格锚点） | 玩家死亡时锚点年龄不足 → `[DeathRewind] no eligible anchor` → 游戏走正常死亡流程（不卡死、不吞死亡） | 通过 |

## 性能改造（响应"游戏内存储明显卡顿"）

| 项 | 改造前 | 改造后 |
|---|---|---|
| 主线程重 IO | `SavesSystem.SaveFile()` 写 ~1 MB 主存档 + 生成备份 | 完全移除（仅在真正回退的阶段 A 执行一次） |
| 容器背包序列化 | 一帧内全部容器 | 分帧摊开：每帧 ≤ `anchorCaptureBatchSize` 个、预算 `anchorCaptureBudgetMs` ms |
| 载荷落盘 | ES3 写盘（主线程） | 后台线程 + 自有格式（`key<TAB>base64`，先写 tmp 再原子改名） |
| 主存档副本 | 主线程 `File.Copy` | 后台线程复制 + 长度校验（不一致重试一次，失败则该锚点不可用 → 自动降级） |
| 重复工作 | 每轮全量重新序列化所有容器 | 内容指纹比对，未被动过的容器复用上次载荷 |

线程安全取舍：后台线程**不调用任何 Unity / 游戏 API**；载荷不再用 ES3（其静态缓存在多线程下不安全）。

元系统语义变化：锚点的元系统快照 = 游戏自己**最后一次落盘**的主存档（后台线程复制），不再强制在锚点时刻落盘。差别仅在于"锚点时刻与游戏上次落盘之间的 EXP/任务变更不会被回退"，与游戏崩溃后读档的结果一致。

## 运行时发现并已修复的缺陷

| 缺陷 | 根因 | 修复 |
|---|---|---|
| Mod 目录不被扫描 | macOS 上 `Application.dataPath` = `<app>/Contents`，不是 `Contents/Resources/Data` | 部署路径改为 `Contents/Mods/DeathRewind`（探针实验确认） |
| 每帧 `NullReferenceException`（`LevelManager.get_LootBoxInventories`） | 该静态属性内部解引用 `LevelManager.Instance`，主菜单阶段为 null | 新增 `Unity/LevelAccess.cs` 统一安全访问 + `LevelReady` 判定 |
| `锚点 N 落盘失败: could not be found in the cache` | `ES3Settings.location = File` 时 `ES3.Save` 直接写盘，额外的 `StoreCachedFile` 必然失败 | 移除 `StoreCachedFile` 调用；无键写入时直接跳过 |
| `玩家背包载荷往返自检失败` | `InventoryData.FromInventory` 跳过空槽，而自检按 `Content.Count` 比较 | 自检改为按"非空物品数"比较 |
| 配置路径错误 | 同上 dataPath 误判 | 改为 `Application.dataPath/ModConfig/DeathRewind/config.txt` |
| 启动即建了一个无意义锚点 | 网格原点未在启动时设定，第一条边界落在 tick=0 | `OnAfterSetup` 即 `ResetGridTo(CurrentTick, CurrentTick)`；建锚点前要求 `LevelAccess.LevelReady` |
| E6 反向复位会把 `dieTask` 置 true | 用 `_death.IsDead` 复位 | 改为复位为 `false`（拦截时原方法体被跳过，dieTask 从未被置位；置 true 会让重放永不执行 → 死亡流程卡死） |

## 待验证 ⏳

需要真实关卡上下文（敌人 / 容器 / 存档）才能验证：

- [ ] 死亡被拦截 → 冻结 → 面板出现 → 选"回退" → 玩家复活
- [ ] 锚点选择正确（`anchor selected id=… age=…`）
- [ ] 阶段 A–E 全部 `result=ok`，`rewind result=ok rewindCount=1`
- [ ] 世界状态回滚（敌人复活 / 容器内容还原 / 门状态 / 世界时间）
- [ ] 元系统回滚（EXP、任务进度回到锚点值）
- [ ] 连续回退（回退后再次死亡仍可选更早锚点）
- [ ] 耗尽降级（`no eligible anchor` / `rewind limit reached`）
- [ ] 失败降级（`forceFailPhase=A..E` 各自注入，断言玩家仍死亡且元系统未变）

自动化辅助开关（仅 `debugMode=true` 生效）：

| 配置项 | 说明 |
|---|---|
| `debugAutoDeathAfter` | 进入关卡 N 秒后自动给自己致命伤害（免按键的端到端验证） |
| `autoRewind` | 死亡后不弹面板，直接回退（用于无人值守验证） |
| `forceFailPhase` | 强制指定阶段抛异常，验证降级语义 |
| `logVerbose` | 输出 `phase=PRE` / `phase=POST` 逐实体明细 |
