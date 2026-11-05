# DeathRewind（死亡回溯）

《逃离鸭科夫 / Escape from Duckov》Unity Mono Mod。死亡时在原生 **YOU DIED** 界面获得一次回溯机会，
回到约 60 秒前的自己（位置、朝向、血量还原，**世界与物品不回滚**）。

| 项 | 值 |
|---|---|
| 程序集 / 入口类型 | `DeathRewind` / `DeathRewind.ModBehaviour` |
| 目标框架 | `net472`（Harmony 2.x + 原生 Mod 加载） |
| 当前版本 | v1.1.0（工作区未提交，见下节） |
| 代码结构索引 | [`docs/CODE_MAP.md`](docs/CODE_MAP.md) |
| 实机验证清单 | [`docs/superpowers/plans/2026-10-20-death-rewind-verification.md`](docs/superpowers/plans/2026-10-20-death-rewind-verification.md) |
| 创意工坊页面草稿 | [`docs/mod_page.html`](docs/mod_page.html) |

## 构建与测试

```bash
bash tools/build.sh                    # 拉 Harmony → Core 纯度校验 → 构建 → 部署到游戏 Mod 目录
dotnet test tests/DeathRewind.Tests/DeathRewind.Tests.csproj
bash tools/check-core-purity.sh        # Core/ 层禁止引用 Unity 与游戏程序集
```

`GAME_DIR` / `MODS_DIR` 可用环境变量覆盖；`WORKSHOP_DEPLOY=1` 额外同步到 Steam 创意工坊目录。

---

## 最近改动（v1.1.0，工作区未提交）

本轮改动围绕**「击杀积分换 1UP」这条玩法链**做了重构，并顺手修掉了三个独立的运行时缺陷
（双实例并行、回溯后血条为空、缩略图与玩家实际画面不一致）。

### 1. 击杀积分：分档计分 + 取消每关次数上限

| 变更 | 位置 |
|---|---|
| 新增击杀分类与积分换算（纯逻辑）：按 `characterPreset.nameKey` 分档 **BOSS 55 / 风暴生物 25 / 雇佣兵·游荡者 15 / 其他 5**，BOSS 优先；带★精英（头顶五角星）在所属档位上额外 **+5** | `src/DeathRewind/Core/KillScore.cs`（新增） |
| `ScoreState` 重写：**取消每关 1UP 上限**（`Cap` / `SetCap` / `AtCap` / `ScoreFull` / `EffectiveCap` 全部移除），满 100 分必发 1UP、**溢出分数结转**（95 + 55 = 150 → 发放后剩 50） | `src/DeathRewind/Core/ScoreState.cs` |
| 锚点环不再有每关次数门槛（`AnchorRing` 构造/`Reconfigure` 去掉 `maxRewinds` 参数，`CanRewind()` 删除） | `src/DeathRewind/Core/AnchorRing.cs` |
| 敌人登记时固化 `nameKey` 与 `isBossCharacter`（角色销毁后问不到），击杀事件改为 `(id, isBoss, nameKey)` | `src/DeathRewind/Unity/EntitySampler.cs` |
| 配置项 `maxRewinds` 全面下线：`ModConfig` 不再读取/校验，面板不再写回，旧 `config.txt` 里该行**原样保留无副作用** | `src/DeathRewind/Config/ModConfig.cs` |

**计分前提变了**：`ScoreState.AddKill` 现在要求**本关已死亡用掉过回溯机会**（`UpsUsed > 0`）才计分——
先丢机会、再靠击杀赚回，不能没死就预攒。

`nameKey` 是游戏自身的敌人类型判据：原生击杀统计 `SavesCounter.AddKillCount` 与击杀任务
`QuestTask_KillCount` 都用它匹配敌人类型，取值与本地化表 `Cname_*` 键一致。

### 2. 复活冷却：起算点从「用掉机会」改为「重新获得 1UP」

- 冷却不再在 `ConsumeRewind()`（回溯成功）时启动，改为在**积分满 100、发放 1UP 的那一刻**启动。
- 冷却期内这枚 1UP **不可用**（HUD 硬币保持删除线 + 倒计时），归零后才变回可用。
- 于是冷却是多次复活的**唯一**限制：次数不再设上限（`RewindCooldown` 类本身未改，只改了调用点）。

### 3. HUD「1UP」指示器：从两态改为四态

`RewindIndicator.SetAvailable(bool)` → `SetState(ScoreState, visible, ready, remainSeconds)`：

| 状态 | 表现 |
|---|---|
| 可用 | 硬币图标全亮 + 文字 `1UP` |
| 冷却中 | 硬币置灰 + 删除线 `1UP` + 倒计时 `Ns` |
| 用掉（仅增强模式） | 挂锁图标 + 删除线 `1UP` + 当前积分 `45/100` |
| 隐藏 | 默认模式用掉即消失；基地（Base）与锚点环未就绪时一律隐藏 |

挂锁图标为程序化生成的贴图（`BuildLockTexture`），不依赖游戏素材；TMP 开启富文本以支持
`<s>` 删除线，并用文案/颜色缓存跳过无变化的重建。

### 4. 设置面板改版（Ctrl+N）

- **增强模式开关移到面板最上方**（它是下面所有数据与设置的总开关）。
- **得分**改为一条进度条（填充 = 积分/100，数值叠在条中央，进行中=琥珀色、满 100 已发放=绿色）。
- 新增只读的**积分规则**表（分值直接引用 `KillScore` 常量，改表自动同步，含带★精英 +5）。
- **移除「每关 1UP 上限」设置项**；1UP 存量与冷却倒计时不再在面板展示（交给 HUD 硬币承载）。
- **复活冷却多一档「无冷却」（0 秒）**：档位 0 / 30 / 60 / 90。
- 面板宽度 484，高度自适应屏幕（`min(660, 屏幕高-48)`）。

### 5. 修复：同进程重复实例并行（新增 `InstanceGuard`）

Steam 创意工坊副本与本地 `Contents/Mods` 副本会被游戏**同时加载**——两份是不同的程序集实例，
各自建锚点环、各自抓缩略图、各自驱动一枚 HUD 硬币，于是「用掉回溯机会后状态栏仍然显示 1UP」。

`src/DeathRewind/Unity/InstanceGuard.cs`（新增）在装补丁**之前**做仲裁：

- 扫描**行为体实例**（`Resources.FindObjectsOfTypeAll`）——**不能按程序集扫**：Mod DLL 是
  `Assembly.Load(byte[])` 动态加载的，Mono 的 `AppDomain.GetAssemblies()` 不返回字节加载的程序集。
- 判据取自游戏基类 `ModBehaviour.info`（`ModInfo.path` / `dllPath`），优先级：
  **本地 `Contents/Mods` 副本 > DLL 写入时间较新 > 程序集 MVID 字典序**。
- 胜出者反射调用落败者的 `DeathRewindStandDown()`（卸补丁 + 停摆 + 清 UI）；对方是旧版本没有该方法时，
  回退为卸载其 Harmony 补丁并禁用其行为体。
- `ModBehaviour` 侧新增 `_stoodDown` 开关，`Update` / `OnGUI` 全部短路。

### 6. 修复：回溯后 HUD 状态条填充为空

左下角 HP / 水 / 耐力条（`Duckov.UI.BarDisplays.BarDisplayController` 家族）的文本立即生效，
但填充走 DOTween（`DOFillAmount`，scaled time）——回溯冻结窗口内补发事件产生的「充能回满」tween
拿不到推进时间，还可能被结算界面关闭路径的 `DOTween.Kill` 杀掉 → **文本满值、填充空置**。

新增 `DeathInterceptor.FixHudBarFills()`：反射读控制器 `Current/Max`，**绕过事件与 tween 直接写
`fill.fillAmount`**。`PostReviveFixup` 执行一次，解冻后的 `PostReviveGuard` 前 1.5 秒再复检校正。

### 7. 修复：缩略图与玩家实际画面不一致

- 新增 `src/DeathRewind/Compat/UrpCameraCompat.cs`：把主相机的 URP 附加数据
  （`UniversalAdditionalCameraData`：renderer 索引、后处理、AA、深度/颜色纹理等 16 个字段）
  抄到抓图相机。`Camera.CopyFrom` 只复制 `Camera` 组件属性，不碰这个组件 → 抓图相机会退回
  URP 默认值。用反射实现，**不编译期依赖 URP 程序集**，字段缺失时静默降级（只影响观感）。
- 抓图相机不进 URP 管线的相机列表（否则会跟着主相机一起渲到屏幕上）。
- RT 宽高比固定，按主相机的水平 FOV 反算垂直 FOV，避免横向被裁。
- `ThumbnailCapture.Tick()`：后台线程编码完 PNG 后，把纹理交回主线程销毁
  （`Object.Destroy` 只能在主线程调用）。
- 每关打一条抓图状态日志（次数、尺寸、宽高比、FOV、URP 同步摘要），便于实机核对。

### 8. 音效：增强模式改用 FMOD core 播内嵌 WAV

`OneUpSound` 新增 `PlayWav(float)`：走 `FMODUnity.RuntimeManager.CoreSystem.playSound`
（master 通道组，跟随游戏音量/静音）播放内嵌 `oneup.wav`，Unity `AudioSource` 仅作兜底。
默认模式的「1UP」提示音仍 post 游戏自带 FMOD 事件 `UI/level_up`。

### 9. 兼容性检查：与其它 Mod 抢资源 / 抢快捷键

新增一套**运行时兼容性扫描**——判定逻辑是纯 C#（可单测），采集逻辑走反射与 Harmony 补丁表：

| 新增 | 位置 |
|---|---|
| 判定规则与结论模型（纯逻辑）：`ModCompatAnalyzer` + `CompatReport` / `CompatIssue` / `HotkeySpec` / `HotkeyClaim` | `src/DeathRewind/Core/ModCompatReport.cs`（新增） |
| 采集真实证据：已激活 Mod 列表、Harmony 补丁表重叠、0Harmony 版本、快捷键占用、config.txt vs 框架存档 | `src/DeathRewind/Compat/ModCompatScanner.cs`（新增） |
| 启动时自动扫一次并写日志（设置面板不再展示兼容性区块） | `ModBehaviour.RefreshCompatReport()` |

能检出的冲突：

| 冲突 | 判据 | 级别 |
|---|---|---|
| 本 Mod 多份副本同时加载 | `ModManager.modInfos` 中 `name == DeathRewind` 的条数 > 1 | 错误 |
| **Harmony 补丁目标重叠** | 同一方法上同时挂着本 Mod（`com.deathrewind.*`）与其它 owner 的补丁 | 警告 |
| 进程内多份 0Harmony | `AppDomain` 里 `0Harmony` 版本去重后 > 1 | 警告 |
| **快捷键完全重叠** | 主键 + 修饰键集合相同，且生效场景相交 | 警告 |
| **快捷键副作用** | 组合键含修饰键，而对方把该修饰键单独绑定 | 警告 |
| **设置来源不一致** | `config.txt` 的值 ≠ ModConfig 框架存档的值（框架值优先 → 改 config.txt 不生效） | 警告 |

**已知边界（检不出）**：其它 Mod 的快捷键（没有跨 Mod 声明机制，面板会列出本 Mod 占用的组合键供人工核对）；
别的 0Harmony 实例里的补丁（不同 `PatchProcessor` 互不可见）；其它 IMGUI 面板与本 Mod 面板的位置重叠。

游戏自身快捷键（反编译核实，用于判定副作用）：`Ctrl` / `C` = 蹲下/起身，`E` = 冲刺/爬梯/情境交互，
`Q` = 攀爬上升，`Escape` = 关闭部分调试界面，`Return` / `BackQuote` = 输入框确认/关闭。
→ 因此本 Mod 的 `Ctrl+N` **必然顺带触发蹲下**（已知且无害的副作用，扫描会把它报成警告，可据此决定是否换组合键）。

### 10. 文档

- `docs/CODE_MAP.md`：同步全部类型签名、配置项、流程图与设计红线；新增 **§12「游戏程序集 API 备忘」**
  （`ilspycmd` 反编译方法 + `CharacterMainControl` / `CharacterRandomPreset` / `DamageInfo` 等
  已核实的成员清单）与 **§13「兼容性检查（与其它 Mod）」**（能检出什么 / 检不出什么 / 游戏快捷键表）。
- `docs/mod_page.html`：更新设置面板说明、积分规则、配置表（移除 `maxRewinds`）与更新日志。
- `docs/superpowers/plans/2026-10-20-death-rewind-verification.md`：同步实机验证清单。

### 11. 测试

| 文件 | 变更 |
|---|---|
| `tests/DeathRewind.Tests/KillScoreTests.cs`（新增） | 分档优先级、风暴衍生体兜底、精英族 nameKey、未知/空 nameKey 兜底、分值常量与中文描述 |
| `tests/DeathRewind.Tests/ModCompatReportTests.cs`（新增） | 快捷键解析（`Ctrl+N` vs 裸 `Ctrl`）、完全重叠 vs 场景不相交、修饰键副作用、重复副本→错误、补丁重叠→警告、设置来源比较（bool/int/float/string） |
| `tests/DeathRewind.Tests/ScoreStateTests.cs` | 重写：未用掉机会不计分、溢出结转、无次数上限、模式关闭不计分等 |
| `tests/DeathRewind.Tests/AnchorRingTests.cs` | 跟随 `AnchorRing` 构造签名变更 |

---

## 兼容性说明

- `config.txt` 的 `maxRewinds` 行**不再读取**，旧配置可原样保留；`enhancedMode` / `rewindCooldown` 语义不变。
- `enhancedMode` 默认 `false` → 默认行为仍是**每关一次复活、不计分、无冷却**。
- 存档格式与公共 API 未变；`ScoreState` / `AnchorRing` 的构造函数签名有破坏性变更，
  已同步更新 `docs/CODE_MAP.md` 与全部调用点与测试。
- **与其它 Mod 的兼容**：本 Mod 自带运行时兼容性扫描（见上文第 9 节），
  启动写日志、游戏内 `Ctrl+N` 面板底部可查；判定规则见 `docs/CODE_MAP.md` §13。

## 本轮验证状态

| 项目 | 状态 |
|---|---|
| `bash tools/build.sh` | ✅ 0 警告 0 错误，已部署到本地 `Contents/Mods/DeathRewind` |
| `dotnet test` | ✅ 128 通过 / 0 失败（含 26 条兼容性判定用例） |
| Core 纯度校验 | ✅ 通过（`ModCompatReport` 只用 .NET 基类库） |
| 兼容性**判定逻辑** | ✅ 单测覆盖（快捷键解析/重叠/副作用、重复副本、补丁重叠、设置来源比较） |
| 兼容性**采集逻辑**（读 Harmony 补丁表、Mod 列表、框架存档） | ⚠️ 只能进游戏验证——本轮**未实机执行** |
| 实机验证（双实例仲裁、HUD 四态、状态条校正、缩略图一致性、兼容性扫描输出） | ⏳ **未在本轮执行**——需按 `docs/superpowers/plans/2026-10-20-death-rewind-verification.md` 的清单在游戏内核对 |

> ⚠️ 改动**必须重启游戏**才生效：程序集在进程启动时就已加载进内存，构建产物写盘时间晚于
> 进程启动时间 = 改动没生效。核对方法与创意工坊副本的坑见 `docs/CODE_MAP.md` §10.1。
