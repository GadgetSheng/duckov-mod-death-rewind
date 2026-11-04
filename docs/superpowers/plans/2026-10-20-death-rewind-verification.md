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
| `锚点 N 落盘失败: could not be found in the cache` | `ES3Settings.location = File` 时 `ES3.Save` 直接写盘，额外的 `StoreCachedFile` 必然失败 | 移除 `StoreCachedFile` 调用；无键写入时直接跳过；载荷改用自有格式（`key<TAB>base64`） |
| `玩家背包载荷往返自检失败` | `InventoryData.FromInventory` 跳过空槽，而自检按 `Content.Count` 比较 | 自检改为按"非空物品数"比较 |
| 配置路径错误 | 同上 dataPath 误判 | 改为 `Application.dataPath/ModConfig/DeathRewind/config.txt` |
| 启动即建了一个无意义锚点 | 网格原点未在启动时设定，第一条边界落在 tick=0 | `OnAfterSetup` 即 `ResetGridTo(CurrentTick, CurrentTick)`；建锚点前要求 `LevelAccess.LevelReady` |
| E6 反向复位会把 `dieTask` 置 true | 用 `_death.IsDead` 复位 | 改为复位为 `false`（拦截时原方法体被跳过，dieTask 从未被置位；置 true 会让重放永不执行 → 死亡流程卡死） |
| **锚点里一条敌人数据都没有**（`world diff enemies=0`，怪物不复活） | 敌人筛选用 `Health.GetComponent<AICharacterController>()`，但敌人的 Health 与 AI 控制器**不在同一 GameObject** | 改为枚举 `AICharacterController` + `Health.TryGetCharacter()` 解析；敌人索敌目标与 `noticed` 一并入锚点 |
| **敌人复活后没有装备** | `CharacterMainControl.OnDead` 把角色物品转成尸体箱 | 新增 `EnemyLootboxPatch`：敌人的物品不生成尸体箱（玩家的照常掉落） |
| **复活后"无敌"、准星消失** | `Health.Hurt` 遇 `isDead=true` 直接返回 false；原生 `AimMarker.Update` 只在死亡时把准星 alpha 设 0，**从不设回 1** | 复活时强制激活玩家/Health/DamageReceiver 物体、`isDead=false`、`SetInvincible(false)`，并显式恢复 `AimMarker.rootCanvasGroup.alpha = 1`；新增 `revive sanity` 日志 |
| 锚点构建主线程卡顿 | `SavesSystem.SaveFile()` 写 ~1 MB + 备份；容器全量序列化 | 主线程分帧采集（预算 2ms/帧）+ 后台线程纯文件 IO + 未变化容器复用载荷；实测 2690ms → 0–2ms |
| **Boss 突然消失**（零号区·矮鸭） | ① `EntitySampler.EvictStale` 把"处于停用状态"当成"已死可销毁"：游戏 `SetActiveByPlayerDistance` 会在玩家离开 100m 外时把角色根物体 `SetActive(false)`，Mod 在 80s 淘汰时把它 `Destroy` → Boss 永久消失（游戏的刷怪器不会再生成它）<br>② `full` 模式回退差量表把"锚点后刷出"的敌人一律移除 → 锚点前没刷出的 Boss 被删掉且不会再生<br>③ 敌人位姿写在 `Health` 所在物体上，Health 挂子物体时模型会被拽走 | ① 销毁资格收窄为"Mod 自己停用 + 当前停用"（`EnemyPolicy.CanDestroy`），游戏自己停用的只遗忘不销毁<br>② 回退跳过 `isBossCharacter` 的实体（保留并记入未覆盖清单）<br>③ 位姿/停用改写角色根物体；移除敌人时从 `SetActiveByPlayerDistance` 名单摘除，避免被游戏重新激活 |
| **子场景里死亡时界面没有回溯能力** | P2 把候选改成跨空间后，死亡入口的"跨地图保护"仍要求 `anchor.SceneKey == 当前场景` → 跨空间候选**一律被取消**（`anchor scene mismatch`）→ 走原生死亡流程，结算界面没有回溯按钮（主场景里正常，因为候选同空间） | 判据改成"目标锚点是否属于**当前关卡**"（`LevelAccess.IsSpaceInCurrentLevel`），跨空间交给 `PhaseTransfer` 传送；同时新增 `ModLog.LogImportant` 决策级日志（发布版也输出）：回溯机会用尽 / 无合格锚点 / 候选列表 / 跨关卡取消 / 控件注入结果 / 回溯结果 |
| **跨空间回溯缺失** | 候选只取"当前场景"，玩家在山洞里死亡时无法回到主地图的老位置；且没有"目标空间属于本关卡"的过滤，也没有跨空间传送的执行段 | ① 新增 `AnchorRing.SelectByAgeAcrossSpaces`：当前空间优先，不够时用本关卡其它空间补位<br>② 跨空间时回溯按钮补空间名标注（"回溯 60 秒 · 零号区"），时间轴节点只标秒数<br>③ 恢复流程新增 `PhaseTransfer`：`MultiSceneCore.LoadAndTeleport(sceneID, 锚点坐标)` 送玩家进目标空间，排在覆盖存档之后、状态还原之前；传送前恢复 `timeScale`（黑屏动画走 scaled 时间） |
| **子场景（山洞/地道）回溯不支持** | ① 旧实现把"切空间"当成"新实例"：`HandleSubSceneLoaded` 一律丢弃该空间的锚点与起点锚点<br>② 锚点环是全局容量 16，进洞后主场景锚点被挤掉<br>③ 起点锚点只按固定时间窗采集：游戏是**子场景加载之后**才把玩家传送到入口，窗口不够时会把"洞外旧坐标"记成起点，回溯把玩家传到非法位置<br>④ 候选只取"当前场景"，且没有"目标空间是否属于本关卡"的过滤 | ① 切空间**保留**锚点（实体活在主场景、坐标固定 → 旧锚点仍有效），只有换关卡才清空并在新关卡起点重设<br>② 锚点容量改为「每空间配额 16 + 全局上限 32」，空间之间不互相挤掉<br>③ 起点锚点改为"玩家坐标稳定"判定（连续 0.5s 位移 <0.5m）<br>④ 新增 `LevelAccess.IsSpaceInCurrentLevel`：跨关卡（换地图）的锚点不作为候选；兜底候选改为"该空间最老的锚点" |
| **炸毁的桶被还原**（进出山洞后桶复原） | 可破坏物（爆炸桶、木箱、障碍）也带 `Health` 组件，被 Mod 当成"敌人"登记 → 进入锚点差量表 → `full` 模式回退时 `ReviveEnemy` 把它复活（桶复原）；同时旧的"失活即销毁"逻辑会销毁已炸毁桶的 GameObject | ① 登记敌人时用 `EnemyPolicy.IsEnemy` 过滤：有角色或有 AI 控制器才算敌人，可破坏物不进入锚点/差量表，炸毁状态永远保持<br>② 上一行的"只销毁 Mod 自己停用的对象"同时避免销毁已炸毁桶的 GameObject |

## 待验证 ⏳

需要真实关卡上下文（敌人 / 容器 / 存档）才能验证：

- [ ] 死亡面板出现**原生 YOU DIED 结算页 + 注入的回溯按钮**（已驱动原生界面，UI 与缩略图位置待目视确认）
- [ ] 回溯按钮上方显示**锚点时刻的缩略图**
- [ ] 锚点选择正确（`anchor selected id=… age=…`），进图不足 60s 时目标为**起点锚点**
- [ ] 阶段 A–E 全部 `result=ok`，`rewind result=ok rewindCount=1`
- [ ] 世界状态回滚：敌人复活（`world diff enemies=` 不再是 0）、**敌人装备还在**、容器内容还原、门状态、世界时间
- [ ] 复活后准星恢复、玩家可被敌人正常伤害（查 `revive sanity` 日志）
- [ ] 元系统回滚（EXP、任务进度回到锚点值）
- [ ] 连续回退（回退后再次死亡仍可选更早锚点）
- [ ] 耗尽降级（`no eligible anchor` / `rewind limit reached`）
- [ ] 跨地图保护（切换地图后回溯历史被清空，无法回溯到上一张图）
- [ ] 失败降级（`forceFailPhase=A..E` 各自注入，断言玩家仍死亡且元系统未变）
- [ ] **Boss 不再消失**（零号区·矮鸭）：进入公路营地看到 Boss → 走开 >100m（游戏会把它停用）→ 停留 >80s → 回到营地，Boss 仍在（修复前会永久消失）；`full` 模式回退后 Boss 也仍在（`enemy:…:boss-kept`）
- [ ] **时间轴四个节点**：死亡结算界面时间轴显示 60/50/40/30 秒四个节点（默认选中 60 秒），预览画面、按钮秒数、实际回溯目标三者一致；拖到别的节点后三者同步切换
- [ ] **子场景死亡仍有回溯能力**：进神秘山洞 → 被风暴怪打死 → 结算界面应出现回溯按钮（时间轴节点只显示秒数，跨空间时回溯按钮文案带空间名）；发布版日志应出现 `[DeathRewind] 场景 … 回溯候选 N 个：…`
- [ ] **跨空间回溯**：在神秘山洞里死亡 → 时间轴出现主地图的 60/50/40/30 节点（节点标签只显示秒数）→ 选中 → 回溯 → 玩家被传送回主地图的老位置并复活（黑屏过渡一次）
- [ ] **子场景可回溯**：进神秘山洞 → 停留 ≥40s → 死亡 → 结算界面出现 60/50/40/30 四个节点 → 回溯 → 玩家在洞内复活（位置在洞内、血量恢复、不弹回主场景）；出洞回主场景后主场景锚点仍在
- [ ] **切空间不丢锚点**：进洞 → 出洞 → 再进洞 → 洞内锚点未被清空（`subscene loaded: ... anchors=` 数量不减）
- [ ] **换关卡清空**：零号区 → 仓库区 → 农场镇，每换一张图锚点清零并在新图起点重建起点锚点；旧图锚点不可用于回溯
- [ ] **炸毁的桶不再复原**：炸掉一个爆炸桶 → 进出山洞（子场景）→ 桶仍是炸毁状态；`full` 模式回退后桶也不会复原（可破坏物不进入锚点差量表）
- [ ] **「1UP」提示音**：进关（`OnLevelInitialized`）后应听到一次「1UP」提示音，且每关只响一次；主菜单不响；换关卡（再进一局）再响一次；`oneUpSound=false` 时不响、`oneUpSoundVolume` 调小后音量随之变化（提示音走 Unity `AudioSource`，不受游戏 FMOD 总线音量影响）
- [ ] **Ctrl+I 设置面板**：游戏内按 Ctrl+I（macOS 也可 Cmd+I）打开面板 → 显示「得分」「1UP（用掉的画删除线）」「冷却」；点「增强模式」切换 → 关闭时上限/冷却两行置灰不可点、得分显示「未开启（增强模式）」；开启后改 `1/2/3` 与 `30/60/90` 立即生效；config.txt 里出现 `enhancedMode=` / `maxRewinds=` / `rewindCooldown=` 三行且其它行未被破坏；关掉再开面板数值保持
- [ ] **默认行为不变（未开启增强模式）**：每关仍只有一次复活（用掉后死亡不再提供回溯）、不计分、无冷却；面板显示「未开启」
- [ ] **击杀积分（需开启增强模式）**：打死 20 只普通生物（5 分 × 20 = 100）→ 面板得分清零并 +1UP（听到提示音）；打死 BOSS（50 分）→ 面板 +50；换关卡后得分归零
- [ ] **多次复活（需开启增强模式）**：把上限调到 3、攒够 100 分 +1UP → 死亡后仍能回溯；回溯成功后 HUD 1UP 指示消失、面板出现删除线的「×1」并开始冷却倒计时；冷却期内再死亡 → 不提供回溯（放行原生死亡）；冷却结束后再次死亡 → 又能回溯（时间轴节点年龄应随使用次数扩展为 120/110/100/90）

自动化辅助开关（仅 `debugMode=true` 生效）：

| 配置项 | 说明 |
|---|---|
| `debugAutoDeathAfter` | 进入关卡 N 秒后自动给自己致命伤害（免按键的端到端验证） |
| `autoRewind` | 死亡后不弹面板，直接回退（用于无人值守验证） |
| `forceFailPhase` | 强制指定阶段抛异常，验证降级语义 |
| `logVerbose` | 输出 `phase=PRE` / `phase=POST` 逐实体明细 |
