# AGENTS.md — mod-duckov (DeathRewind)

> 项目级规则文件，供 omp / Claude Code / Cursor 等 harness 读取。
> 第一节优先级最高，不可被后续章节稀释，但**仅在模型供应商为 minimax 时生效**。

---

## 1. 计费模式约束（仅当模型供应商为 minimax 时生效）

**适用前提**：当前模型供应商为 minimax——以会话系统提示中的模型标识为准
（如 `minimax-code-cn/MiniMax-M3.1-Flash`），或 `omp config` 中 `modelRoles.default`
指向 `minimax-*`。

- 供应商**不是** minimax → 忽略本节全部约束，按常规模式工作；不得因「省额度」
  而牺牲任务质量或必要的澄清。
- 供应商**是** minimax → 完整执行本节。
- 子代理若使用不同 provider，则其上下文不适用本节，除非其模型同样属于 minimax。

当前模型服务按「调用次数 / 额度窗口」计费（MiniMax Token Plan / M Plan 类）：

- 成本单位是一次模型往返（用户 prompt + 该轮全部工具调用循环），而非 token 数量。
- 额度挂在滚动窗口上（通常 5 小时窗口 + 周窗口），到期重置、不累积。

行为必须遵循：

1. **满载输出**：一次响应完成用户要求的全部内容。禁止拆分任务、禁止
   「先给概要再补充」、禁止以「需要我继续吗」结尾。
2. **合并请求**：多个独立子任务合并进同一次输出。能一次生成的就不要分两次。
3. **工具最小化**：能用一次工具调用完成的读取/处理绝不分多次；相互独立的工具
   调用必须并行发在同一轮，不得串行等待。
4. **禁止确认往返**：能直接执行就直接执行。仅在操作不可逆（删除 / 发布 / 支付 /
   提交）或目标存在真实歧义时才询问；其余情况采用最合理默认值并在结果中
   说明假设。
5. **上下文复用**：优先引用已加载内容，不重复注入相同上下文；长对话主动利用
   prompt cache。
6. **慎用委派**：子代理、多轮规划、反复自检都会额外消耗往返，默认在主循环内
   一次完成。
7. **完成标准**：以「用户要求的全部字段 / 数量 / 范围 / 格式均已满足」为结束
   条件，而不是「看起来差不多」。

### 动态配额注入

若会话开头能取得用量面板，把真实数字注入为一行上下文（占位符替换即可）：

```text
[配额状态] 5h 窗口已用 77%（53 分钟后重置）| 周窗口已用 57% | 缓存命中率 95.8%
```

分级策略：

| 窗口余量 | 行为模式 |
| --- | --- |
| > 60% | 正常模式：质量优先，允许必要的多轮工具循环 |
| 20% ~ 60% | 紧凑模式：合并子任务、并行工具、压缩中间说明 |
| < 20% | 省电模式：单轮完成全部交付，跳过一切非必要调用与自检 |

> 本机 `omp usage` 目前返回 “No usage data”（provider 无 usage endpoint），
> 故无法自动注入；如后续 provider 提供面板，按上表手动注入。

---

## 2. 项目速览

- 目标：《逃离鸭科夫》/ Escape from Duckov（Unity Mono + 原生 Mod 加载 + Harmony 2.x）
- 框架：`net472`，程序集 `DeathRewind`，入口类型 `DeathRewind.ModBehaviour`
- 代码：`src/DeathRewind`，结构索引见 `docs/CODE_MAP.md`
- 设计规格：`docs/superpowers/specs/`，验证计划：`docs/superpowers/plans/`

目录约定：

- `Core/` — 纯 C# 逻辑，**禁止**引用 Unity / 游戏程序集（由 `tools/check-core-purity.sh` 强制）
- `Unity/` — Unity / 游戏 API 适配层
- `Harmony/` — Harmony 补丁
- `UI/` — 原生 YOU DIED 界面注入、HUD 指示、IMGUI 降级面板
- `Config/`、`Compat/` — 配置与反射兼容层

## 3. 常用命令

```bash
# 完整构建 + 部署到游戏 Mod 目录（先拉 Harmony、再校验 Core 纯度）
bash tools/build.sh

# 仅测试
dotnet test tests/DeathRewind.Tests/DeathRewind.Tests.csproj

# 仅 Core 纯度校验
bash tools/check-core-purity.sh
```

`GAME_DIR` / `MODS_DIR` 可用环境变量覆盖，默认指向本机 Steam 路径。

## 4. 改动约定

- 新增逻辑先判断归属层：`Core`（可单测、无引擎依赖）/ `Unity`（引擎适配）/
  `Harmony`（补丁）/ `UI`。Core 层新类型必须在 `tests/DeathRewind.Tests/` 有对应测试。
- 修改游戏私有字段访问统一走 `Compat/PrivateStateAccessor.cs`，不要在别处散落反射。
- 日志统一走 `ModLog`，不要直接 `Debug.Log`。
- 交付前必须实际运行 `dotnet build`（或 `tools/build.sh`）与 `dotnet test`，
  贴出真实输出；测试通过不等于功能正确，涉及运行时行为需给出实机验证说明。
- 保持公共 API / 存档格式向后兼容；破坏性变更需在同一次输出中同步更新
  文档（`docs/CODE_MAP.md`）与相关计划文档。
