# 死亡回档 Mod（Death Rewind）实施计划

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为《逃离鸭科夫》做一个 Mod：战斗中被击杀时冻结并弹出确认框，选择"回退"把玩家与世界状态回滚到约 120–180 秒前。

**Architecture:** 纯 C# 的 `Core/`（DTO、`RewindClock`、`AnchorRing`、`LiveStateStore`）承载全部可单测的规则；Unity 侧适配层（`Unity/`、`Harmony/`、`UI/`、`Compat/`）负责与游戏对象打交道。恢复流程分 A–F 阶段，失败经阶段 F 补偿，保证"玩家终态互斥""物品与元系统完整性""世界侧无混合态"三条不变量。

**Tech Stack:** C# / `net472`（Mod，匹配 Unity Mono 与 Harmony 2.4.1 的 net472 构建）/ `net10.0`（单测，本机仅有 .NET 10 运行时）/ Harmony 2.4.1（仓库内置 `vendor/0Harmony.dll`）/ EasySave3 + UniTask + ItemStatsSystem（**全部引用游戏自带 DLL，不打包**）/ Unity 2022.2+ Mono / xunit

**规格来源:** `docs/superpowers/specs/2026-10-20-death-rewind-design.md`（第 6 版终版）

**与规格的已核实偏差（以本计划为准）：**

1. Mod 根目录：规格 §3 写 `Duckov.app/Contents/Mods/<Mod>/`（源自社区文档）；**代码为准** —— `ModManager.DefaultModFolderPath = Path.Combine(Application.dataPath, "Mods")`，macOS 即 `Duckov.app/Contents/Resources/Data/Mods/<Mod>/`。Task 14 用游戏内 Mods 界面的"复制路径"按钮校验。
2. 网格原点归属：规格 §6.2 把网格原点放在 `RewindClock`；本计划收进 `AnchorRing` 独占（时钟只管有效 tick），避免两条时间线漂移。规格 §6.2/§6.5 语义不变。
3. 时钟 API：规格写 `Tick(float)`，实现用 `Tick(double)`。
4. 背包载荷：规格写"ES3 序列化的 InventoryData"，实现用 `JsonUtility`（`InventoryData`/`ItemTreeData` 均为 `[Serializable]` 且字段为具体类型，JsonUtility 可用），载荷仍是**不透明字节串**。

**环境约定（实施时按实际情况替换）:**

```
GAME_DIR="/Users/gadget/Library/Application Support/Steam/steamapps/common/Escape from Duckov"
MANAGED_DIR="$GAME_DIR/Duckov.app/Contents/Resources/Data/Managed"
MODS_ROOT="$GAME_DIR/Duckov.app/Contents/Resources/Data/Mods"   # = Application.dataPath/Mods
MODS_DIR="$MODS_ROOT/DeathRewind"
```

**工作目录:** 本仓库是专用新仓库（`mod-duckov`），无需额外 worktree。

---

## 文件结构

| 路径 | 职责 |
|---|---|
| `src/DeathRewind/DeathRewind.csproj` | Mod 工程（`net472`）；引用 `vendor/0Harmony.dll` 与游戏 DLL |
| `vendor/0Harmony.dll` | Harmony 2.4.1 的 net472 构建（`tools/fetch-harmony.sh` 下载） |
| `src/DeathRewind/Core/Dto.cs` | 纯 C# DTO 与 `Anchor` 记录定义（无 Unity/游戏引用） |
| `src/DeathRewind/Core/RewindClock.cs` | 纯逻辑时钟：有效 tick、冻结挂起、年龄计算 |
| `src/DeathRewind/Core/AnchorRing.cs` | 纯逻辑锚点环：网格时间线、入环/出环、Id 分配、目标选择、已消耗集合、回退计数 |
| `src/DeathRewind/Core/LiveStateStore.cs` | 实体状态字典、深拷贝、按锚点整体替换 |
| `src/DeathRewind/Unity/LevelHooks.cs` | 关卡生命周期事件适配与"场景切换 vs 子场景加载"判别 |
| `src/DeathRewind/Unity/EntitySampler.cs` | 枚举关卡实体 → DTO；稳定 ID 注册表；敌人登记/延迟销毁/出环释放/超时淘汰 |
| `src/DeathRewind/Unity/InventoryPayload.cs` | 背包 ↔ 不透明字节串的序列化/还原 + 往返自检 |
| `src/DeathRewind/Unity/AnchorBuilder.cs` | 按指定 Id 与时刻构建锚点内容并落盘 |
| `src/DeathRewind/Unity/MetaRestore.cs` | 主存档原子覆盖/回滚 + `SetFile` 重载 |
| `src/DeathRewind/Unity/WorldRestorer.cs` | 恢复阶段 A–F 与正/反向差量表应用 |
| `src/DeathRewind/Harmony/HarmonyGuard.cs` | Harmony 版本冲突检测（注册前拦截） |
| `src/DeathRewind/Harmony/DeathInterceptor.cs` | 拦截死亡、冻结、裁决、放行与重放、重入保护 |
| `src/DeathRewind/Harmony/EnemyPersistencePatch.cs` | 接管敌人 `DestroyOnDelay`，保住对象待复活 |
| `src/DeathRewind/UI/RewindPrompt.cs` | IMGUI 确认面板（**只能在 `OnGUI` 中绘制**） |
| `src/DeathRewind/Compat/PrivateStateAccessor.cs` | 集中反射访问游戏私有字段（`dieTask`/`hasHandledDeath`/`Health.isDead`/`TimeOfDayController.time`） |
| `src/DeathRewind/Config/ModConfig.cs` | `key=value` 配置读写与一致性校验 |
| `src/DeathRewind/ModBehaviour.cs` | Mod 入口：装配、主循环、时间注入、`timeScale` 收口、`OnGUI` 面板、快捷键 |
| `src/DeathRewind/info.ini` / `preview.png` | Mod 元数据与图标 |
| `tests/DeathRewind.Tests/` | 纯逻辑单测（`net10.0`，**直接链接 `Core/*.cs`，不引用 Mod 工程**） |
| `tools/build.sh` | 构建 + 部署到 Mods 目录 |
| `tools/fetch-harmony.sh` | 下载 Harmony 2.4.1 的 net472 构建到 `vendor/` |
| `tools/check-core-purity.sh` | 检查 `Core/` 不得引用 Unity / 游戏 / ES3 |
| `docs/superpowers/plans/2026-10-20-death-rewind-verification.md` | 游戏内验证记录（Task 15 创建） |

---

## Chunk 1: 工程骨架与纯逻辑 Core

### Task 1: 工程骨架、依赖获取与构建脚本

**Files:**
- Create: `src/DeathRewind/DeathRewind.csproj`
- Create: `src/DeathRewind/info.ini`
- Create: `src/DeathRewind/preview.png`
- Create: `tools/fetch-harmony.sh`
- Create: `tools/build.sh`
- Create: `tools/check-core-purity.sh`
- Create: `tests/DeathRewind.Tests/DeathRewind.Tests.csproj`
- Create: `tests/DeathRewind.Tests/SmokeTests.cs`

- [ ] **Step 1: 获取 Harmony（net472 构建）**

`tools/fetch-harmony.sh`：

```bash
#!/usr/bin/env bash
# 下载 Lib.Harmony 2.4.1 的 net472 构建到 vendor/（该包没有 netstandard TFM）
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENDOR="$ROOT/vendor"
mkdir -p "$VENDOR"
if [ -f "$VENDOR/0Harmony.dll" ]; then echo "already present"; exit 0; fi
TMP="$(mktemp -d)"
curl -sSL "https://api.nuget.org/v3-flatcontainer/lib.harmony/2.4.1/lib.harmony.2.4.1.nupkg" -o "$TMP/h.nupkg"
unzip -o -q "$TMP/h.nupkg" "lib/net472/0Harmony.dll" -d "$TMP/x"
cp "$TMP/x/lib/net472/0Harmony.dll" "$VENDOR/0Harmony.dll"
rm -rf "$TMP"
echo "fetched $VENDOR/0Harmony.dll"
```

Run: `bash tools/fetch-harmony.sh && ls -la vendor/`
Expected: 打印 `fetched .../vendor/0Harmony.dll`，文件约 2.4 MB。

- [ ] **Step 2: 创建 Mod 工程文件**

`src/DeathRewind/DeathRewind.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AssemblyName>DeathRewind</AssemblyName>
    <RootNamespace>DeathRewind</RootNamespace>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <DuckovManagedDir Condition="'$(DuckovManagedDir)' == ''">/Users/gadget/Library/Application Support/Steam/steamapps/common/Escape from Duckov/Duckov.app/Contents/Resources/Data/Managed</DuckovManagedDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- net472 需要参考程序集包（跨平台可用，无需本机装 Mono） -->
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <!-- Harmony 2.4.1 没有 netstandard TFM，固定用 net472 构建；不随包发布，由 tools/build.sh 复制 -->
    <Reference Include="0Harmony">
      <HintPath>$(MSBuildThisFileDirectory)../../vendor/0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>

    <!-- 全部引用游戏自带的那一份，禁止随 Mod 打包 -->
    <Reference Include="TeamSoda.Duckov.Core">
      <HintPath>$(DuckovManagedDir)/TeamSoda.Duckov.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ItemStatsSystem">
      <HintPath>$(DuckovManagedDir)/ItemStatsSystem.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="EasySave3">
      <HintPath>$(DuckovManagedDir)/EasySave3.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UniTask">
      <HintPath>$(DuckovManagedDir)/UniTask.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(DuckovManagedDir)/UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.IMGUIModule">
      <HintPath>$(DuckovManagedDir)/UnityEngine.IMGUIModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

> 目标框架选 `net472`：Unity 2022 的 Mono 运行时可直接加载 net472 程序集；Harmony 2.4.1 只提供 net35/net452/net472/net48/net5.0+ 构建，没有 netstandard2.0，用 `netstandard2.1` + `PackageReference` 会报 NU1202。

- [ ] **Step 3: 创建 info.ini 与 preview.png**

`src/DeathRewind/info.ini`（游戏按**扁平 key=value** 解析；`[section]` 行会被跳过，为避免歧义不写段头）：

```ini
name=DeathRewind
displayName=死亡回档
description=战斗中死亡时可回退到约 120-180 秒前的世界状态
version=0.1.0
tags=gameplay,qol
```

> Mod 加载契约（已核实 `Duckov.Modding.ModManager`）：DLL 必须命名为 `<name>.dll`；同目录需要 `info.ini` 与 `preview.png`；入口类型必须是 `<name>.ModBehaviour` 且继承 `Duckov.Modding.ModBehaviour`；游戏通过 `Setup()` → `OnAfterSetup()` 初始化、`NotifyBeforeDeactivate()` → `OnBeforeDeactivate()` 卸载。

生成 `preview.png`（游戏侧按 `preview.png` 读取，未见尺寸校验；256×256 纯色即可）：

Run:
```bash
cd src/DeathRewind && python3 - <<'PY'
import struct, zlib
w=h=256
raw=b''.join(b'\x00'+bytes([40,60,90,255])*w for _ in range(h))
def chunk(t,d):
    c=t+d
    return struct.pack('>I',len(d))+c+struct.pack('>I',zlib.crc32(c)&0xffffffff)
png=b'\x89PNG\r\n\x1a\n'
png+=chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))
png+=chunk(b'IDAT',zlib.compress(raw))
png+=chunk(b'IEND',b'')
open('preview.png','wb').write(png)
print('preview.png', len(png), 'bytes')
PY
```
Expected: 打印 `preview.png <N> bytes`。

- [ ] **Step 4: 创建单测工程（不引用 Mod 工程）**

`tests/DeathRewind.Tests/DeathRewind.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>disable</Nullable>
    <LangVersion>9.0</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <!-- 直接链接 Core 源文件：单测不触碰 Unity / 游戏程序集 -->
    <Compile Include="../../src/DeathRewind/Core/**/*.cs" LinkBase="Core" />
  </ItemGroup>
</Project>
```

> 本机只安装了 .NET 10 运行时（`dotnet --list-runtimes` 实测只有 `Microsoft.NETCore.App 10.0.13`），故测试工程用 `net10.0`。
> 用 `<Compile Include>` 而非 `ProjectReference`：Mod 工程引用了 Unity / 游戏 DLL，单测不需要它们。

`tests/DeathRewind.Tests/SmokeTests.cs`（**Task 1 用占位断言**；Task 2 创建了 `Dto.cs` 后改为断言 `Anchor` 类型；Task 3 创建 `RewindClock.cs` 后再补其类型断言）：

```csharp
using Xunit;

namespace DeathRewind.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void Core_Sources_Are_Linked()
        {
            Assert.True(true);
        }
    }
}
```

- [ ] **Step 5: 创建纯度检查与构建脚本**

`tools/check-core-purity.sh`：

```bash
#!/usr/bin/env bash
# Core/ 只允许引用 .NET 基类库：禁止 Unity / 游戏程序集 / EasySave3
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORE="$ROOT/src/DeathRewind/Core"
fail=0
# 1) using 指令
if grep -RInE 'using[[:space:]]+(UnityEngine|Duckov|Saves|ItemStatsSystem|TeamSoda|EasySave3|Cysharp)' "$CORE"; then
  echo "FAIL: Core/ 出现了禁止的 using" >&2; fail=1
fi
# 2) 全限定名（防止 using 之外的逃逸）
if grep -RInE '\b(UnityEngine|ItemStatsSystem|EasySave3|Duckov|Saves|TeamSoda|Cysharp)\.' "$CORE"; then
  echo "FAIL: Core/ 出现了禁止的全限定类型引用" >&2; fail=1
fi
[ "$fail" -eq 0 ] && echo "OK: Core purity"
exit "$fail"
```

`tools/build.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${GAME_DIR:-/Users/gadget/Library/Application Support/Steam/steamapps/common/Escape from Duckov}"
MANAGED_DIR="$GAME_DIR/Duckov.app/Contents/Resources/Data/Managed"
MODS_DIR="${MODS_DIR:-$GAME_DIR/Duckov.app/Contents/Mods/DeathRewind}"

bash "$ROOT/tools/fetch-harmony.sh"
bash "$ROOT/tools/check-core-purity.sh"

dotnet build "$ROOT/src/DeathRewind/DeathRewind.csproj" -c Release -p:DuckovManagedDir="$MANAGED_DIR"

OUT="$ROOT/src/DeathRewind/bin/Release"          # AppendTargetFrameworkToOutputPath=false
mkdir -p "$MODS_DIR"
cp "$OUT/DeathRewind.dll" "$MODS_DIR/"
cp "$ROOT/src/DeathRewind/info.ini" "$MODS_DIR/"
cp "$ROOT/src/DeathRewind/preview.png" "$MODS_DIR/"
cp "$ROOT/vendor/0Harmony.dll" "$MODS_DIR/"        # 单份、路径钉死（net472）
echo "deployed to $MODS_DIR"
ls -la "$MODS_DIR"
```

- [ ] **Step 6: 验证构建、纯度与单测跑得起来**

Run:
```bash
chmod +x tools/*.sh
bash tools/check-core-purity.sh
dotnet build src/DeathRewind/DeathRewind.csproj -c Release
dotnet test tests/DeathRewind.Tests/
```
Expected:
- `OK: Core purity`
- Mod 工程构建成功（0 error）。若报 `NU1202`/`NU1701`，说明目标框架或引用方式不对——**不要**改回 `netstandard2.1` + `PackageReference Lib.Harmony`（该包无 netstandard TFM）。
- `dotnet test` 输出 **1 passed**（SmokeTests 的占位断言），证明单测工程不依赖 Unity 引用。

- [ ] **Step 7: 提交**

```bash
git add src/DeathRewind/DeathRewind.csproj src/DeathRewind/info.ini src/DeathRewind/preview.png \
        tests/DeathRewind.Tests tools/build.sh tools/check-core-purity.sh tools/fetch-harmony.sh
git commit -m "chore: 工程骨架、依赖获取、构建与纯度检查脚本"
```

---

### Task 2: DTO 定义（Core/Dto.cs）

**Files:**
- Create: `src/DeathRewind/Core/Dto.cs`
- Modify: `tests/DeathRewind.Tests/SmokeTests.cs`（占位断言改为 `typeof(DeathRewind.Core.Anchor)` 断言）

- [ ] **Step 1: 写 DTO**

```csharp
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

        public EnemyState Clone() => new EnemyState
        {
            Position = (float[])Position.Clone(),
            Rotation = (float[])Rotation.Clone(),
            Health = Health,
            Alive = Alive,
            AiTargetId = AiTargetId,
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
```

- [ ] **Step 2: 验证编译与纯度，更新 SmokeTests**

Run: `bash tools/check-core-purity.sh && dotnet build src/DeathRewind/DeathRewind.csproj -c Release && dotnet test tests/DeathRewind.Tests/`
Expected: `OK: Core purity`；构建成功；1 passed。

把 `SmokeTests.cs` 的断言改为：

```csharp
Assert.NotNull(typeof(DeathRewind.Core.Anchor));
```

再跑一次 `dotnet test`，Expected: 1 passed。

- [ ] **Step 3: 提交**

```bash
git add src/DeathRewind/Core/Dto.cs tests/DeathRewind.Tests/SmokeTests.cs
git commit -m "feat(core): DTO 与锚点记录定义"
```

---

### Task 3: RewindClock（有效 tick 与冻结挂起）

**Files:**
- Create: `src/DeathRewind/Core/RewindClock.cs`
- Create: `tests/DeathRewind.Tests/RewindClockTests.cs`
- Modify: `tests/DeathRewind.Tests/SmokeTests.cs`（补 `RewindClock` 类型断言）

- [ ] **Step 1: 写失败测试**

```csharp
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class RewindClockTests
    {
        [Fact]
        public void Tick_AccumulatesEffectiveTime()
        {
            var clock = new RewindClock();
            clock.Tick(10.0);
            clock.Tick(12.5);
            Assert.Equal(12.5, clock.CurrentTick, 6);
            Assert.Equal(12.5, clock.RawTime, 6);
        }

        [Fact]
        public void Freeze_SuspendsEffectiveTimeButKeepsRawTime()
        {
            var clock = new RewindClock();
            clock.Tick(10.0);
            clock.Freeze();
            clock.Tick(20.0);
            Assert.Equal(10.0, clock.CurrentTick, 6);   // 冻结时长不计入
            Assert.Equal(20.0, clock.RawTime, 6);
            clock.Resume();
            clock.Tick(21.0);
            Assert.Equal(11.0, clock.CurrentTick, 6);
        }

        [Fact]
        public void Age_IsUnaffectedByHowLongClockHasRun()
        {
            var clock = new RewindClock();
            clock.Tick(300.0);
            Assert.Equal(200.0, clock.Age(100.0), 6);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/DeathRewind.Tests/ --filter RewindClockTests`
Expected: 编译错误 `RewindClock` 不存在（红灯原因必须是"类型不存在"）。

- [ ] **Step 3: 实现**

`src/DeathRewind/Core/RewindClock.cs`：

```csharp
namespace DeathRewind.Core
{
    /// <summary>
    /// 纯逻辑时钟。有效 tick（CurrentTick）在冻结期间挂起；锚点年龄由此计算。
    /// 网格时间线由 AnchorRing 独占，本类不涉及网格原点。
    /// </summary>
    public sealed class RewindClock
    {
        private double _lastRaw;

        public double CurrentTick { get; private set; }
        public double RawTime { get; private set; }
        public bool IsFrozen { get; private set; }

        public void Tick(double rawTime)
        {
            double delta = rawTime - _lastRaw;
            if (delta < 0) delta = 0;
            _lastRaw = rawTime;
            RawTime = rawTime;
            if (IsFrozen) return;
            CurrentTick += delta;
        }

        public void Freeze() => IsFrozen = true;

        public void Resume()
        {
            IsFrozen = false;
            _lastRaw = RawTime;
        }

        public double Age(double absTimestamp) => CurrentTick - absTimestamp;
    }
}
```

> 时钟**不提供 Reset**：有效 tick 必须自 Mod 启动起单调，否则关卡切换时 tick 跳变会让网格边界一次性补建多个锚点（每次都触发 `SaveFile()` + 落盘）。关卡切换时只重置网格原点（见 `AnchorRing.ResetGridTo`）。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/DeathRewind.Tests/ --filter RewindClockTests`
Expected: 3 passed。

- [ ] **Step 5: 提交**

```bash
git add src/DeathRewind/Core/RewindClock.cs tests/DeathRewind.Tests/RewindClockTests.cs tests/DeathRewind.Tests/SmokeTests.cs
git commit -m "feat(core): 纯逻辑时钟 RewindClock"
```

---

### Task 4: LiveStateStore（实体状态、深拷贝、按锚点整体替换）

**Files:**
- Create: `src/DeathRewind/Core/LiveStateStore.cs`
- Create: `tests/DeathRewind.Tests/LiveStateStoreTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class LiveStateStoreTests
    {
        [Fact]
        public void Snapshot_IsDeepCopy()
        {
            var store = new LiveStateStore();
            var player = new PlayerState { Health = 50f, Alive = true };
            player.BagPayload = new byte[] { 1, 2, 3 };
            store.SetPlayer(player);

            var snapshot = store.Snapshot();

            snapshot.GetPlayer().Health = 10f;
            snapshot.GetPlayer().BagPayload[0] = 99;

            Assert.Equal(50f, store.GetPlayer().Health);
            Assert.Equal(1, store.GetPlayer().BagPayload[0]);
        }

        [Fact]
        public void GetEnemy_MissingKey_ReturnsNull()
        {
            var store = new LiveStateStore();
            store.SetEnemy("enemy:1", new EnemyState { Health = 30f, Alive = true });
            Assert.True(store.GetEnemy("enemy:1").Alive);
            Assert.Null(store.GetEnemy("enemy:999"));
        }

        [Fact]
        public void ReplaceFrom_OverwritesLiveStateWithAnchor()
        {
            var store = new LiveStateStore();
            store.SetPlayer(new PlayerState { Health = 10f });
            store.SetEnemy("enemy:1", new EnemyState { Health = 5f });

            var anchor = new Anchor
            {
                Player = new PlayerState { Health = 88f },
                Enemies = new System.Collections.Generic.Dictionary<string, EnemyState>
                {
                    ["enemy:2"] = new EnemyState { Health = 44f },
                },
            };
            store.ReplaceFrom(anchor);

            Assert.Equal(88f, store.GetPlayer().Health);
            Assert.Null(store.GetEnemy("enemy:1"));
            Assert.NotNull(store.GetEnemy("enemy:2"));
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/DeathRewind.Tests/ --filter LiveStateStoreTests`
Expected: 编译错误 `LiveStateStore` 不存在。

- [ ] **Step 3: 实现**

`src/DeathRewind/Core/LiveStateStore.cs`：

```csharp
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

        /// <summary>回退成功后，用锚点状态整体替换实时状态（规格 §6.7）。</summary>
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
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/DeathRewind.Tests/ --filter LiveStateStoreTests`
Expected: 3 passed。

- [ ] **Step 5: 提交**

```bash
git add src/DeathRewind/Core/LiveStateStore.cs tests/DeathRewind.Tests/LiveStateStoreTests.cs
git commit -m "feat(core): 实时状态字典、深拷贝与按锚点整体替换"
```

---

### Task 5: AnchorRing（网格时间线、Id 分配、目标选择、回退计数）

**Files:**
- Create: `src/DeathRewind/Core/AnchorRing.cs`
- Create: `tests/DeathRewind.Tests/AnchorRingTests.cs`

语义约定（写死在实现与测试里）：

- **网格原点**：`ResetGridTo(gridTimestamp, currentTick)` 把网格时间线原点设为 `gridTimestamp`，并记录"当前 tick 对应网格时间戳 = gridTimestamp"。此后网格时间戳 = `gridTimestamp + (currentTick − 原点 tick)`。
- **网格边界**：网格原点**本身不是**边界；第一条边界在 `gridTimestamp + interval`。
- **锚点 Id**：`AllocateId()` 预分配 Id（供构建阶段命名文件）；`Insert` **不覆盖**已分配的 Id。
- **目标选择**：合格锚点 = 年龄 ≥ `minAge` 且未被用作恢复目标；取其中 **AbsTimestamp 最大**（最新）者。
- **回退后**：移除 AbsTimestamp **大于**目标锚点的锚点（被丢弃时间线），更早的锚点保留。
- **tick 来源唯一**：`CurrentTick` 由 `SetTick` 写入；`SelectTarget()` / `CanRewind()` 直接读它，不再接受外部 tick 参数（避免两条 tick 来源漂移）。

- [ ] **Step 1: 写失败测试**

```csharp
using System.Collections.Generic;
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class AnchorRingTests
    {
        private static Anchor Make(double abs, double grid)
            => new Anchor { AbsTimestamp = abs, GridTimestamp = grid };

        [Fact]
        public void PollDueGridTimestamp_FiresOncePerInterval()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            ring.ResetGridTo(0.0, 0.0);
            Assert.Null(ring.PollDueGridTimestamp());            // 原点不是边界
            ring.SetTick(60.0);
            Assert.Equal(60.0, ring.PollDueGridTimestamp().Value);
            Assert.Null(ring.PollDueGridTimestamp());
            ring.SetTick(120.0);
            Assert.Equal(120.0, ring.PollDueGridTimestamp().Value);
        }

        [Fact]
        public void AllocateId_IsMonotonic_AndInsertKeepsIt()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            int id = ring.AllocateId();
            var a = new Anchor { Id = id };
            ring.Insert(a, new List<Anchor>());
            Assert.Equal(id, a.Id);
            Assert.True(ring.AllocateId() > id);
        }

        [Fact]
        public void Insert_AssignsId_WhenNotPreallocated()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var a = new Anchor();
            ring.Insert(a, new List<Anchor>());
            Assert.True(a.Id > 0);
        }

        [Fact]
        public void Insert_EvictsOldestBeyondCapacity()
        {
            var ring = new AnchorRing(2, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(10, 10), evicted);
            ring.Insert(Make(20, 20), evicted);
            Assert.Empty(evicted);
            ring.Insert(Make(30, 30), evicted);
            Assert.Single(evicted);
            Assert.Equal(10.0, evicted[0].AbsTimestamp);
        }

        [Fact]
        public void SelectTarget_PicksNewestWithAgeAtLeastMinAge()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(0, 0), evicted);
            ring.Insert(Make(60, 60), evicted);
            ring.Insert(Make(120, 120), evicted);
            ring.Insert(Make(180, 180), evicted);
            ring.SetTick(300.0);
            var target = ring.SelectTarget();
            Assert.NotNull(target);
            Assert.Equal(180.0, target.AbsTimestamp);   // 合格者取最新
        }

        [Fact]
        public void SelectTarget_SkipsTooYoungAnchors()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            ring.Insert(Make(200, 200), evicted);      // age 100 < 120
            ring.Insert(Make(120, 120), evicted);      // age 180
            ring.SetTick(300.0);
            var target = ring.SelectTarget();
            Assert.NotNull(target);
            Assert.Equal(120.0, target.AbsTimestamp);
        }

        [Fact]
        public void CommitRewind_ConsumesTarget_RemovesYounger_KeepsOlder()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            var older = Make(60, 60);
            var target = Make(120, 120);
            var younger = Make(180, 180);
            ring.Insert(older, evicted);
            ring.Insert(target, evicted);
            ring.Insert(younger, evicted);

            var removed = new List<Anchor>();
            ring.CommitRewind(target, removed);

            Assert.Equal(1, ring.RewindCount);
            Assert.True(target.Consumed);
            Assert.Single(removed);
            Assert.Equal(180.0, removed[0].AbsTimestamp);

            // 更早的锚点仍可作为下一次回退目标（规格 §6.5 / 判据 4）
            ring.SetTick(300.0);
            var next = ring.SelectTarget();
            Assert.NotNull(next);
            Assert.Equal(60.0, next.AbsTimestamp);
        }

        [Fact]
        public void CanRewind_RespectsMaxRewinds()
        {
            var ring = new AnchorRing(4, 120.0, 1, 60.0);
            var evicted = new List<Anchor>();
            var a = Make(120, 120);
            ring.Insert(a, evicted);
            ring.CommitRewind(a, new List<Anchor>());
            ring.SetTick(300.0);
            Assert.False(ring.CanRewind());
        }

        [Fact]
        public void GridOriginReset_KeepsExistingAnchorsSelectable()
        {
            var ring = new AnchorRing(4, 120.0, 3, 60.0);
            var evicted = new List<Anchor>();
            var a = Make(60, 60);
            ring.Insert(a, evicted);
            ring.SetTick(300.0);

            ring.ResetGridTo(60.0, 300.0);

            // 网格原点重置不改变已有锚点的年龄 → 仍可选中
            Assert.NotNull(ring.SelectTarget());
            Assert.Equal(60.0, ring.SelectTarget().AbsTimestamp);
            Assert.Equal(240.0, ring.CurrentTick - a.AbsTimestamp, 6);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/DeathRewind.Tests/ --filter AnchorRingTests`
Expected: 编译错误 `AnchorRing` 不存在。

- [ ] **Step 3: 实现**

`src/DeathRewind/Core/AnchorRing.cs`：

```csharp
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

        private double _currentTick;

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

        /// <summary>回退成功：消耗目标锚点、移除更年轻的锚点（被丢弃时间线）、递增计数。</summary>
        public void CommitRewind(Anchor target, List<Anchor> removed)
        {
            if (target == null) return;
            target.Consumed = true;
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
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/DeathRewind.Tests/ --filter AnchorRingTests`
Expected: 9 passed。

- [ ] **Step 5: 跑全量单测**

Run: `dotnet test tests/DeathRewind.Tests/`
Expected: 全部通过（1 + 3 + 3 + 9 = 16 个用例）。

- [ ] **Step 6: 提交**

```bash
git add src/DeathRewind/Core/AnchorRing.cs tests/DeathRewind.Tests/AnchorRingTests.cs
git commit -m "feat(core): 锚点环（网格时间线/Id 分配/目标选择/回退计数）"
```

---

## Chunk 2: Unity 侧适配（配置、生命周期、采样、构建、存档、恢复）

> 本 Chunk 的代码无法脱离 Unity 单测；每个 Task 以「编译通过 + 明确的日志输出 + Chunk 3 的游戏内验证」为验收标准。
> 所有游戏类型成员（`Health.IsDead`、`Inventory.Content`、`AICharacterController.searchedEnemy` 等）以反编译结果为准；实施时若签名不符，按实际签名调整并在此处记录。

### Task 6: 配置、关卡生命周期适配与 Mod 入口

**Files:**
- Create: `src/DeathRewind/Config/ModConfig.cs`
- Create: `src/DeathRewind/Unity/LevelHooks.cs`
- Create: `src/DeathRewind/ModBehaviour.cs`

- [ ] **Step 1: 实现 `ModConfig`（`key=value` 文本）**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DeathRewind.Config
{
    public sealed class ModConfig
    {
        public bool Enabled = true;
        public double AnchorInterval = 60;
        public int AnchorCount = 4;
        public double MinRewindAge = 120;
        public int MaxRewinds = 3;
        public double SampleInterval = 0.25;
        public bool AutoRewind;
        public string RewindHotkey = "F9";
        public string ContinueHotkey = "Escape";
        public bool DebugMode;
        public string ForceFailPhase = "";
        public bool LogVerbose;

        // macOS: Application.dataPath = <app>/Contents/Resources/Data
        //      → ../../ModConfig = <app>/Contents/ModConfig（与游戏现有 ModConfig 目录一致）
        public static string FilePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "ModConfig/DeathRewind/config.txt"));

        public static ModConfig Load()
        {
            var cfg = new ModConfig();
            var path = FilePath;
            Debug.Log($"[DeathRewind] config path: {path}");
            if (File.Exists(path))
            {
                var raw = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    raw[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
                cfg.Enabled = GetBool(raw, "enabled", cfg.Enabled);
                cfg.AnchorInterval = GetDouble(raw, "anchorInterval", cfg.AnchorInterval);
                cfg.AnchorCount = GetInt(raw, "anchorCount", cfg.AnchorCount);
                cfg.MinRewindAge = GetDouble(raw, "minRewindAge", cfg.MinRewindAge);
                cfg.MaxRewinds = GetInt(raw, "maxRewinds", cfg.MaxRewinds);
                cfg.SampleInterval = GetDouble(raw, "sampleInterval", cfg.SampleInterval);
                cfg.AutoRewind = GetBool(raw, "autoRewind", cfg.AutoRewind);
                cfg.RewindHotkey = GetString(raw, "rewindHotkey", cfg.RewindHotkey);
                cfg.ContinueHotkey = GetString(raw, "continueHotkey", cfg.ContinueHotkey);
                cfg.DebugMode = GetBool(raw, "debugMode", cfg.DebugMode);
                cfg.ForceFailPhase = GetString(raw, "forceFailPhase", cfg.ForceFailPhase);
                cfg.LogVerbose = GetBool(raw, "logVerbose", cfg.LogVerbose);
            }
            cfg.Validate();
            return cfg;
        }

        private void Validate()
        {
            if (AnchorInterval <= 0) { Debug.LogWarning("[DeathRewind] anchorInterval 非法，回落 60"); AnchorInterval = 60; }
            if (AnchorCount < 2) { Debug.LogWarning("[DeathRewind] anchorCount 非法，回落 4"); AnchorCount = 4; }
            if (MaxRewinds < 1) { Debug.LogWarning("[DeathRewind] maxRewinds 非法，回落 3"); MaxRewinds = 3; }
            if (SampleInterval <= 0) { Debug.LogWarning("[DeathRewind] sampleInterval 非法，回落 0.25"); SampleInterval = 0.25; }
            if (MinRewindAge > AnchorCount * AnchorInterval)
            {
                Debug.LogWarning($"[DeathRewind] minRewindAge({MinRewindAge}) > anchorCount*anchorInterval({AnchorCount * AnchorInterval})，回退将永远不可用，回落");
                MinRewindAge = AnchorCount * AnchorInterval;
            }
        }

        private static string GetString(Dictionary<string, string> raw, string key, string fallback)
            => raw.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        private static bool GetBool(Dictionary<string, string> raw, string key, bool fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (bool.TryParse(v, out var b)) return b;
            if (v == "1") return true;
            if (v == "0") return false;
            Debug.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }

        private static double GetDouble(Dictionary<string, string> raw, string key, double fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            Debug.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }

        private static int GetInt(Dictionary<string, string> raw, string key, int fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            Debug.LogWarning($"[DeathRewind] 配置项 {key} 值非法（{v}），回落 {fallback}");
            return fallback;
        }
    }
}
```

- [ ] **Step 2: 实现 `LevelHooks`**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeathRewind.Unity
{
    /// <summary>关卡生命周期适配：关卡开始 / 子场景加载 / 场景切换 / 关卡结束。</summary>
    public sealed class LevelHooks
    {
        private readonly HashSet<string> _subScenes = new HashSet<string>();

        public event Action OnLevelStarted;
        public event Action OnSubSceneLoaded;
        public event Action OnLevelEnded;

        public void Install()
        {
            LevelManager.OnLevelInitialized += HandleLevelInitialized;
            MultiSceneCore.OnSubSceneLoaded += HandleSubSceneLoaded;
            SceneLoader.onStartedLoadingScene += HandleSceneLoading;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            RaidUtilities.OnRaidEnd += HandleRaidEnd;
        }

        public void Uninstall()
        {
            LevelManager.OnLevelInitialized -= HandleLevelInitialized;
            MultiSceneCore.OnSubSceneLoaded -= HandleSubSceneLoaded;
            SceneLoader.onStartedLoadingScene -= HandleSceneLoading;
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            RaidUtilities.OnRaidEnd -= HandleRaidEnd;
        }

        private void HandleLevelInitialized()
        {
            _subScenes.Clear();
            OnLevelStarted?.Invoke();
        }

        private void HandleSubSceneLoaded(MultiSceneCore core, Scene scene)
        {
            // 只登记子场景；绝不在此清空锚点环或回退计数
            _subScenes.Add(scene.name);
            OnSubSceneLoaded?.Invoke();
        }

        private void HandleSceneLoading(SceneLoadingContext ctx)
        {
            // 场景切换流程开始；最终由 sceneUnloaded 收口
        }

        private void HandleSceneUnloaded(Scene scene)
        {
            if (_subScenes.Contains(scene.name)) return;   // 进房间 → 不清空
            OnLevelEnded?.Invoke();
        }

        private void HandleRaidEnd(RaidUtilities.RaidInfo info) => OnLevelEnded?.Invoke();
    }
}
```

> 事件签名以反编译结果为准（规格 §3 已核实）。若签名不符，只调整参数列表，**判别语义不得改变**。

- [ ] **Step 3: 实现 `ModBehaviour` 入口**

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using DeathRewind.Config;
using DeathRewind.Core;
using DeathRewind.Harmony;
using DeathRewind.UI;
using DeathRewind.Unity;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DeathRewind
{
    /// <summary>Mod 入口。游戏要求类型名为 &lt;info.ini 的 name&gt;.ModBehaviour 且继承 Duckov.Modding.ModBehaviour。</summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private ModConfig _config;
        private RewindClock _clock;
        private AnchorRing _ring;
        private LiveStateStore _store = new LiveStateStore();
        private LevelHooks _hooks;
        private EntitySampler _sampler;
        private AnchorBuilder _builder;
        private WorldRestorer _restorer;
        private double _nextSampleAt;
        private bool _timeScaleOwned;

        // 对外暴露（DeathInterceptor 需要）
        public AnchorRing Ring => _ring;
        public double CurrentTick => _clock?.CurrentTick ?? 0;
        public ModConfig Config => _config;

        protected override void OnAfterSetup()
        {
            _config = ModConfig.Load();
            Debug.Log($"[DeathRewind] mods root: {Duckov.Modding.ModManager.DefaultModFolderPath}");
            if (!_config.Enabled) { Debug.Log("[DeathRewind] disabled by config"); return; }

            _clock = new RewindClock();
            _ring = new AnchorRing(_config.AnchorCount, _config.MinRewindAge, _config.MaxRewinds, _config.AnchorInterval);
            _sampler = new EntitySampler(_store, _config.AnchorCount, _config.AnchorInterval);
            _builder = new AnchorBuilder(_config, _store, _ring);
            _restorer = new WorldRestorer(_config, _ring, _store);

            // 启动时清理孤儿文件（文件名不对应任何现存锚点）
            AnchorBuilder.CleanupOrphanFiles(new HashSet<int>());

            _hooks = new LevelHooks();
            _hooks.OnLevelStarted += HandleLevelStarted;
            _hooks.OnSubSceneLoaded += HandleSubSceneLoaded;
            _hooks.OnLevelEnded += HandleLevelEnded;
            _hooks.Install();

            DeathInterceptor.Install(this, _config, _restorer);
            EnemyPersistencePatch.Install(_sampler);
            Debug.Log("[DeathRewind] initialized");
        }

        protected override void OnBeforeDeactivate()
        {
            ReleaseTimeScale();
            DeathInterceptor.Uninstall();
            EnemyPersistencePatch.Uninstall();
            _hooks?.Uninstall();
            Debug.Log("[DeathRewind] deactivated");
        }

        private void Update()
        {
            if (_config == null || !_config.Enabled) return;
            _clock.Tick(Time.unscaledTime);
            _ring.SetTick(_clock.CurrentTick);

            // 冻结期间：不采样、不推进网格（规格 §6.2/§6.3/§7 步骤 3）
            if (!_clock.IsFrozen)
            {
                if (_clock.CurrentTick >= _nextSampleAt)
                {
                    _nextSampleAt = _clock.CurrentTick + _config.SampleInterval;
                    _sampler.Sample(_clock.CurrentTick);
                }

                var due = _ring.PollDueGridTimestamp();
                if (due.HasValue)
                {
                    int id = _ring.AllocateId();
                    var sw = Stopwatch.StartNew();
                    var anchor = _builder.Build(id, _clock.CurrentTick, due.Value);
                    sw.Stop();
                    var evicted = new List<Anchor>();
                    _ring.Insert(anchor, evicted);
                    foreach (var old in evicted) _sampler.ReleaseAnchor(old);
                    _sampler.SetAnchorCoverage(_ring.Anchors);
                    Debug.Log($"[DeathRewind] anchor created id={anchor.Id} gridTs={anchor.GridTimestamp} absTs={anchor.AbsTimestamp} buildMs={sw.ElapsedMilliseconds}");
                }
            }

            HandlePromptHotkeys();
            HandleDebugHotkeys();
        }

        /// <summary>IMGUI 只能在 OnGUI 中调用（Update 里调用会每帧抛异常）。</summary>
        private void OnGUI()
        {
            RewindPrompt.Draw();
        }

        private void HandleLevelStarted()
        {
            // 时钟不复位（有效 tick 必须单调），只重置网格原点与状态
            _ring.Clear();
            _ring.ResetGridTo(_clock.CurrentTick, _clock.CurrentTick);
            _store.Clear();
            _sampler.ResetRegistry();
            _nextSampleAt = _clock.CurrentTick;
            Debug.Log($"[DeathRewind] level started: anchors/rewindCount/store reset at tick={_clock.CurrentTick}");
        }

        private void HandleSubSceneLoaded()
        {
            // 只刷新注册表，不清锚点环、不归零回退计数（规格 §8.2）
            _sampler.RefreshRegistry();
            Debug.Log($"[DeathRewind] subscene loaded: anchors={_ring.Anchors.Count} rewindCount={_ring.RewindCount}");
        }

        private void HandleLevelEnded()
        {
            ReleaseTimeScale();
            _ring.Clear();
            _store.Clear();
            _sampler.DestroyAllTrackedEnemies();
            _sampler.ResetRegistry();
            AnchorBuilder.DeleteAllAnchorFiles();
            Debug.Log("[DeathRewind] level ended: state cleared");
        }

        private void HandlePromptHotkeys()
        {
            if (!RewindPrompt.IsOpen) return;
            if (Input.GetKeyDown(ParseKey(_config.RewindHotkey))) RewindPrompt.Submit(true);
            if (Input.GetKeyDown(ParseKey(_config.ContinueHotkey))) RewindPrompt.Submit(false);
        }

        private void HandleDebugHotkeys()
        {
            if (!_config.DebugMode) return;
            if (!Input.GetKeyDown(KeyCode.F10)) return;
            var player = LevelManager.Instance?.MainCharacter;
            if (player == null) return;
            player.Health.Hurt(new DamageInfo
            {
                damageValue = 1e6f,
                finalDamage = 1e6f,
                toDamageReceiver = player.GetComponent<DamageReceiver>(),
                damagePoint = player.transform.position,
            });
        }

        private static KeyCode ParseKey(string name)
            => string.IsNullOrWhiteSpace(name) ? KeyCode.None : (KeyCode)System.Enum.Parse(typeof(KeyCode), name, true);

        // timeScale 收口：任何退出拦截面板的路径都必须经过这里
        public void FreezeTimeScale()
        {
            Time.timeScale = 0f;
            _timeScaleOwned = true;
            _clock?.Freeze();
        }

        public void ReleaseTimeScale()
        {
            if (!_timeScaleOwned) return;
            _timeScaleOwned = false;
            Time.timeScale = 1f;
            _clock?.Resume();
        }

        // 供 DeathInterceptor 调用
        public void ReleaseAnchor(Anchor anchor) => _sampler?.ReleaseAnchor(anchor);
        public void ReleaseCommittedDestroy() => _sampler?.DestroyDeferredEnemies();
    }
}
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 报错仅涉及 Task 7–13 尚未实现的类型（`EntitySampler` / `AnchorBuilder` / `WorldRestorer` / `DeathInterceptor` / `EnemyPersistencePatch`）。若出现别的错误（如 `LevelManager.OnLevelInitialized` 不存在、`DamageInfo` 字段名不符），按反编译结果修正签名后重跑。

- [ ] **Step 5: 提交**

```bash
git add src/DeathRewind/Config/ModConfig.cs src/DeathRewind/Unity/LevelHooks.cs src/DeathRewind/ModBehaviour.cs
git commit -m "feat(unity): 配置、关卡生命周期适配与 Mod 入口"
```

---

### Task 7: EntitySampler（枚举、稳定 ID 注册表、敌人登记与延迟销毁）

**Files:**
- Create: `src/DeathRewind/Unity/EntitySampler.cs`

- [ ] **Step 1: 实现**

```csharp
using System.Collections.Generic;
using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>枚举关卡实体 → DTO；维护稳定 ID 注册表；敌人登记、延迟销毁、出环释放与超时淘汰。</summary>
    public sealed class EntitySampler
    {
        private readonly LiveStateStore _store;
        private readonly int _anchorCount;
        private readonly double _anchorInterval;
        private readonly Dictionary<Health, string> _enemyIds = new Dictionary<Health, string>();
        private readonly Dictionary<GameObject, string> _enemyObjects = new Dictionary<GameObject, string>();
        private readonly Dictionary<Door, string> _doorIds = new Dictionary<Door, string>();
        private readonly Dictionary<string, double> _registeredAt = new Dictionary<string, double>();
        private readonly HashSet<string> _anchorCovered = new HashSet<string>();
        private readonly HashSet<string> _deferredDestroy = new HashSet<string>();
        private int _enemySeq;
        private int _doorSeq;
        private double _now;

        public EntitySampler(LiveStateStore store, int anchorCount, double anchorInterval)
        {
            _store = store;
            _anchorCount = anchorCount;
            _anchorInterval = anchorInterval;
        }

        /// <summary>敌人判定（与 EnemyPersistencePatch 共用）。</summary>
        public static bool IsEnemyCharacter(Health health)
        {
            if (health == null) return false;
            if (health.IsMainCharacterHealth) return false;
            return health.GetComponent<AICharacterController>() != null;
        }

        public void Sample(double now)
        {
            _now = now;
            SamplePlayer();
            SampleEnemies();
            SampleContainers();
            SampleDoors();
            EvictStale();
        }

        private void SamplePlayer()
        {
            var main = LevelManager.Instance?.MainCharacter;
            if (main == null) return;
            _store.SetPlayer(new PlayerState
            {
                Position = ToArray3(main.transform.position),
                Rotation = ToArray4(main.transform.rotation),
                Health = main.Health.CurrentHealth,
                Alive = !main.Health.IsDead,
                BagPayload = InventoryPayload.Capture(main.CharacterItem.Inventory),
            });
        }

        private void SampleEnemies()
        {
            // 1) 发现新实体（只枚举激活对象）
            foreach (var health in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
            {
                if (!IsEnemyCharacter(health)) continue;
                if (_enemyIds.ContainsKey(health)) continue;
                string id = "enemy:" + (++_enemySeq);
                _enemyIds[health] = id;
                _enemyObjects[health.gameObject] = id;
                _registeredAt[id] = _now;
            }

            // 2) 更新状态（必须遍历注册表，否则停用的敌人观测不到）
            foreach (var kv in new List<KeyValuePair<Health, string>>(_enemyIds))
            {
                var health = kv.Key;
                if (health == null) { RemoveEntry(kv.Value, kv.Key); continue; }
                var ai = health.GetComponent<AICharacterController>();
                _store.SetEnemy(kv.Value, new EnemyState
                {
                    Position = ToArray3(health.transform.position),
                    Rotation = ToArray4(health.transform.rotation),
                    Health = health.CurrentHealth,
                    Alive = !health.IsDead,
                    AiTargetId = ResolveTargetId(ai),
                });
            }
        }

        private void SampleContainers()
        {
            var map = LevelManager.Instance?.LootBoxInventories;
            if (map == null) return;
            foreach (var kv in map)
            {
                string key = "loot:" + kv.Key;
                var inv = kv.Value;
                if (inv == null) continue;
                _store.SetContainer(key, new ContainerState
                {
                    Key = key,
                    Capacity = inv.Capacity,
                    LockedIndexes = inv.lockedIndexes != null ? inv.lockedIndexes.ToArray() : new int[0],
                    ItemPayload = InventoryPayload.Capture(inv),
                });
            }
        }

        private void SampleDoors()
        {
            foreach (var door in Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                if (door == null) continue;
                if (!_doorIds.TryGetValue(door, out var id))
                {
                    id = "door:" + (++_doorSeq);
                    _doorIds[door] = id;
                }
                _store.SetDoor(id, new DoorState { Key = id, Closed = !door.IsOpen });
            }
        }

        /// <summary>敌人销毁接管时登记：分配稳定 ID、标记死亡，销毁推迟到提交成功后。</summary>
        public void RegisterDeadEnemy(Health health)
        {
            if (health == null) return;
            if (_enemyIds.TryGetValue(health, out var existing))
            {
                _store.SetEnemy(existing, new EnemyState { Alive = false, Health = 0f });
                return;
            }
            string id = "enemy:" + (++_enemySeq);
            _enemyIds[health] = id;
            _enemyObjects[health.gameObject] = id;
            _registeredAt[id] = _now;
            _store.SetEnemy(id, new EnemyState
            {
                Alive = false,
                Health = 0f,
                Position = ToArray3(health.transform.position),
                Rotation = ToArray4(health.transform.rotation),
            });
        }

        /// <summary>回退中"移除"的敌人：只停用，销毁推迟到提交成功后。</summary>
        public void MarkDeferredDestroy(string enemyId) => _deferredDestroy.Add(enemyId);

        /// <summary>提交成功：销毁本次回退中被移除的敌人对象（失败路径不调用，保持可反向激活）。</summary>
        public void DestroyDeferredEnemies()
        {
            foreach (var id in _deferredDestroy)
            {
                foreach (var kv in _enemyObjects)
                {
                    if (kv.Value != id) continue;
                    if (kv.Key != null && !kv.Key.activeSelf) Object.Destroy(kv.Key);
                }
            }
            _deferredDestroy.Clear();
        }

        /// <summary>关卡结束：销毁全部被跟踪的停用敌人对象。</summary>
        public void DestroyAllTrackedEnemies()
        {
            foreach (var kv in _enemyObjects)
                if (kv.Key != null && !kv.Key.activeSelf) Object.Destroy(kv.Key);
            _deferredDestroy.Clear();
        }

        /// <summary>锚点出环：删除该锚点的载荷/存档文件，并释放它覆盖过的停用敌人对象。</summary>
        public void ReleaseAnchor(Anchor anchor)
        {
            if (anchor == null) return;
            AnchorBuilder.DeleteAnchorFiles(anchor);
            foreach (var kv in _enemyObjects)
            {
                if (kv.Value == null) continue;
                if (!anchor.Enemies.ContainsKey(kv.Value)) continue;
                var go = kv.Key;
                if (go == null) continue;
                if (!go.activeSelf) Object.Destroy(go);
            }
        }

        /// <summary>记录当前锚点覆盖了哪些敌人（用于淘汰判定：被锚点覆盖的敌人不能淘汰）。</summary>
        public void SetAnchorCoverage(IReadOnlyList<Anchor> anchors)
        {
            _anchorCovered.Clear();
            if (anchors == null) return;
            foreach (var a in anchors)
                foreach (var id in a.Enemies.Keys)
                    _anchorCovered.Add(id);
        }

        /// <summary>未被任何锚点覆盖、且登记超过 anchorCount×anchorInterval 的停用敌人 → 淘汰销毁。</summary>
        private void EvictStale()
        {
            double limit = _anchorCount * _anchorInterval;
            var stale = new List<string>();
            foreach (var kv in _registeredAt)
            {
                if (_anchorCovered.Contains(kv.Key)) continue;      // 锚点还要用它复活
                if (_now - kv.Value > limit) stale.Add(kv.Key);
            }
            foreach (var id in stale)
            {
                _registeredAt.Remove(id);
                foreach (var kv in new List<KeyValuePair<GameObject, string>>(_enemyObjects))
                {
                    if (kv.Value != id) continue;
                    if (kv.Key != null && !kv.Key.activeSelf) Object.Destroy(kv.Key);
                    _enemyObjects.Remove(kv.Key);
                }
            }
        }

        public void ResetRegistry()
        {
            _enemyIds.Clear();
            _enemyObjects.Clear();
            _doorIds.Clear();
            _registeredAt.Clear();
            _anchorCovered.Clear();
            _deferredDestroy.Clear();
        }

        /// <summary>子场景加载：剔除已销毁对象的引用，保留其余稳定 ID。</summary>
        public void RefreshRegistry()
        {
            foreach (var kv in new List<KeyValuePair<Health, string>>(_enemyIds))
                if (kv.Key == null) RemoveEntry(kv.Value, kv.Key);
            foreach (var kv in new List<KeyValuePair<Door, string>>(_doorIds))
                if (kv.Key == null) _doorIds.Remove(kv.Key);
        }

        private void RemoveEntry(string id, Health key)
        {
            _registeredAt.Remove(id);
            if (key != null) _enemyIds.Remove(key);
            foreach (var kv in new List<KeyValuePair<GameObject, string>>(_enemyObjects))
                if (kv.Value == id) _enemyObjects.Remove(kv.Key);
        }

        private string ResolveTargetId(AICharacterController ai)
        {
            if (ai == null) return null;
            var target = ai.searchedEnemy != null ? ai.searchedEnemy.gameObject : null;
            if (target == null) return null;
            return _enemyObjects.TryGetValue(target, out var id) ? id : null;   // 目标不在注册表 → null
        }

        private static float[] ToArray3(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] ToArray4(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    }
}
```

要点（对应规格 §5.4）：

- 采样必须遍历注册表（含停用敌人），活跃枚举只用于发现新实体。
- `IsEnemyCharacter` 只认带 `AICharacterController` 的 `Health`，与 `EnemyPersistencePatch` 共用同一判定（避免两处漂移）。
- `EvictStale` 必须跳过 `_anchorCovered` 中的敌人，否则锚点无法复活它们。

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 报错仅涉及 `InventoryPayload`、`AnchorBuilder`、`WorldRestorer`、`DeathInterceptor`、`EnemyPersistencePatch`。

- [ ] **Step 3: 提交**

```bash
git add src/DeathRewind/Unity/EntitySampler.cs
git commit -m "feat(unity): 实体采样、稳定 ID 注册表与敌人延迟销毁"
```

---

### Task 8: 背包载荷与锚点构建

**Files:**
- Create: `src/DeathRewind/Unity/InventoryPayload.cs`
- Create: `src/DeathRewind/Unity/AnchorBuilder.cs`

- [ ] **Step 1: 实现 `InventoryPayload`**

```csharp
using System.Text;
using System.Threading.Tasks;
using ItemStatsSystem;
using ItemStatsSystem.Data;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>背包 ↔ 不透明字节串。InventoryData/ItemTreeData 均为 [Serializable] 且字段为具体类型，JsonUtility 可用。</summary>
    public static class InventoryPayload
    {
        public static byte[] Capture(Inventory inventory)
        {
            if (inventory == null) return null;
            var data = InventoryData.FromInventory(inventory);
            return Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));
        }

        public static InventoryData Parse(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                return JsonUtility.FromJson<InventoryData>(Encoding.UTF8.GetString(payload));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DeathRewind] InventoryPayload 解析失败: {e.Message}");
                return null;
            }
        }

        /// <summary>原地还原：清空后按载荷重建。失败返回 false，调用方负责回灌备份。</summary>
        public static async Task<bool> ApplyAsync(byte[] payload, Inventory target)
        {
            var data = Parse(payload);
            if (data == null || target == null) return false;
            target.DestroyAllContent();
            await InventoryData.LoadIntoInventory(data, target);
            return true;
        }

        /// <summary>往返自检：Capture→Parse 后条目数一致（构建锚点时调用一次）。</summary>
        public static bool VerifyRoundTrip(Inventory inventory)
        {
            if (inventory == null) return true;
            var data = Parse(Capture(inventory));
            if (data == null) return false;
            return data.entries != null && data.entries.Count == inventory.Content.Count;
        }
    }
}
```

> `LoadIntoInventory` 返回 `UniTask`；这里用 `async Task<bool>` 包装，调用方**必须逐个 await 完成**（配合 Task 10 的超时与静默约束）。

- [ ] **Step 2: 实现 `AnchorBuilder`**

```csharp
using System.Collections.Generic;
using System.IO;
using DeathRewind.Core;
using EasySave3;
using Saves;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>按指定 Id 与时刻构建锚点内容并落盘。</summary>
    public sealed class AnchorBuilder
    {
        private readonly Config.ModConfig _config;
        private readonly LiveStateStore _store;
        private readonly AnchorRing _ring;

        public AnchorBuilder(Config.ModConfig config, LiveStateStore store, AnchorRing ring)
        { _config = config; _store = store; _ring = ring; }

        public static string RootDir => Path.Combine(Application.persistentDataPath, "ModRewind");
        public const string BackupFileName = "save_backup.sav";

        public Anchor Build(int id, double absTimestamp, double gridTimestamp)
        {
            // 1) 元系统刷新：让主存档反映锚点时刻的 EXP/任务/经济
            SavesSystem.CollectSaveData();
            SavesSystem.SaveFile();

            var anchor = new Anchor
            {
                Id = id,
                AbsTimestamp = absTimestamp,
                GridTimestamp = gridTimestamp,
                Player = _store.GetPlayer()?.Clone(),
                World = new WorldState { TimeOfDay = TimeOfDayController.Time },
            };

            foreach (var key in _store.EnemyKeys) anchor.Enemies[key] = _store.GetEnemy(key)?.Clone();
            foreach (var key in _store.ContainerKeys) anchor.Containers[key] = _store.GetContainer(key)?.Clone();
            foreach (var key in _store.DoorKeys) anchor.Doors[key] = _store.GetDoor(key)?.Clone();

            // 2) 载荷与主存档副本落盘（文件名与锚点 Id 对应）
            Directory.CreateDirectory(RootDir);
            anchor.ContainerPayloadPath = Path.Combine(RootDir, $"anchor_{id}.es3");
            anchor.MainSaveCopyPath = Path.Combine(RootDir, $"save_{id}.sav");
            SavePayloads(anchor);
            CopyMainSave(anchor.MainSaveCopyPath);
            return anchor;
        }

        private static void SavePayloads(Anchor anchor)
        {
            var settings = new ES3Settings
            {
                location = ES3.Location.File,
                path = anchor.ContainerPayloadPath,
            };
            ES3.DeleteFile(anchor.ContainerPayloadPath, settings);
            foreach (var kv in anchor.Containers)
            {
                if (kv.Value?.ItemPayload == null) continue;
                ES3.Save(kv.Key, Convert.ToBase64String(kv.Value.ItemPayload), settings);
            }
            if (anchor.Player?.BagPayload != null)
                ES3.Save("player", Convert.ToBase64String(anchor.Player.BagPayload), settings);
            ES3.StoreCachedFile(anchor.ContainerPayloadPath, settings);
        }

        private static void CopyMainSave(string dest)
        {
            string src = Path.Combine(Application.persistentDataPath, SavesSystem.GetFilePath(SavesSystem.CurrentSlot));
            File.Copy(src, dest, true);
        }

        public static void DeleteAnchorFiles(Anchor anchor)
        {
            if (anchor == null) return;
            TryDelete(anchor.ContainerPayloadPath);
            TryDelete(anchor.MainSaveCopyPath);
        }

        /// <summary>关卡结束：清理锚点文件，但**保留** save_backup.sav（阶段 B/F 的回滚源）。</summary>
        public static void DeleteAllAnchorFiles()
        {
            if (!Directory.Exists(RootDir)) return;
            foreach (var f in Directory.GetFiles(RootDir))
            {
                if (Path.GetFileName(f) == BackupFileName) continue;
                TryDelete(f);
            }
        }

        /// <summary>启动时清理孤儿文件（文件名不对应任何现存锚点）。</summary>
        public static void CleanupOrphanFiles(HashSet<int> liveIds)
        {
            if (!Directory.Exists(RootDir)) return;
            foreach (var f in Directory.GetFiles(RootDir))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name == Path.GetFileNameWithoutExtension(BackupFileName)) continue;
                var parts = name.Split('_');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var id) && liveIds.Contains(id)) continue;
                TryDelete(f);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (System.Exception e) { Debug.LogWarning($"[DeathRewind] 删除 {path} 失败: {e.Message}"); }
        }
    }
}
```

> 载荷统一用 **Base64 字符串**存入 ES3（不用 `byte[]` 重载，避免两种文件格式并存导致读错）。
> `SavesSystem.GetFilePath(slot)` 是公开静态方法（规格 §3 已核实）。

- [ ] **Step 3: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 报错仅涉及 `WorldRestorer`、`DeathInterceptor`、`EnemyPersistencePatch`。

- [ ] **Step 4: 提交**

```bash
git add src/DeathRewind/Unity/InventoryPayload.cs src/DeathRewind/Unity/AnchorBuilder.cs
git commit -m "feat(unity): 背包载荷序列化与锚点构建落盘"
```

---

### Task 9: MetaRestore（主存档原子覆盖/回滚与元系统重载）

**Files:**
- Create: `src/DeathRewind/Unity/MetaRestore.cs`

- [ ] **Step 1: 实现**

```csharp
using System.IO;
using Saves;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>主存档的原子覆盖/回滚 + 元系统重载。</summary>
    public sealed class MetaRestore
    {
        /// <summary>把磁盘上的 Save_{slot}.sav 原子替换为 sourcePath 的内容，再触发元系统重载。</summary>
        public bool OverwriteFrom(string sourcePath)
        {
            string targetAbs = Path.Combine(Application.persistentDataPath, SavesSystem.GetFilePath(SavesSystem.CurrentSlot));
            string tmp = targetAbs + ".tmp";
            string bak = Path.Combine(AnchorBuilder.RootDir, "atomic_replace.bak");   // 备份留在 mod 目录，不污染游戏 Saves
            try
            {
                File.Copy(sourcePath, tmp, true);
                if (File.Exists(targetAbs))
                {
                    // 覆盖式原子替换（不先删除目标，避免"主存档已删、tmp 还没移过去"的窗口）
                    File.Replace(tmp, targetAbs, bak, true);
                }
                else
                {
                    File.Move(tmp, targetAbs);
                }
                SavesSystem.SetFile(SavesSystem.CurrentSlot);   // 重新 CacheFile + 触发 OnSetFile
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DeathRewind] 覆盖主存档失败: {e.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        /// <summary>把当前主存档备份到 mod 私有目录，返回备份路径。</summary>
        public string BackupCurrent()
        {
            Directory.CreateDirectory(AnchorBuilder.RootDir);
            string targetAbs = Path.Combine(Application.persistentDataPath, SavesSystem.GetFilePath(SavesSystem.CurrentSlot));
            string backup = Path.Combine(AnchorBuilder.RootDir, AnchorBuilder.BackupFileName);
            File.Copy(targetAbs, backup, true);
            return backup;
        }
    }
}
```

要点：

- **不要**手动 `ES3.CacheFile(backupPath)`：那会让缓存键指向备份文件，与游戏读主存档的键错位（规格 §8.3）。回滚一律走"备份 → 原子写回主存档路径 → `SavesSystem.SetFile(CurrentSlot)`"。
- `File.Replace` 的备份文件放在 mod 目录，不留在游戏的 Saves 目录里。

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 报错仅涉及 `WorldRestorer`、`DeathInterceptor`、`EnemyPersistencePatch`。

- [ ] **Step 3: 提交**

```bash
git add src/DeathRewind/Unity/MetaRestore.cs
git commit -m "feat(unity): 主存档原子覆盖与元系统重载"
```

---

### Task 10: WorldRestorer（阶段 A–F 与差量表应用）

**Files:**
- Create: `src/DeathRewind/Unity/WorldRestorer.cs`

- [ ] **Step 1: 实现阶段编排与统一超时包装**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeathRewind.Core;
using Saves;
using UnityEngine;

namespace DeathRewind.Unity
{
    public enum RestorePhase { A, B, C, D, E, E2, E3, E4, E5, E6, F }

    public sealed class WorldRestorer
    {
        private readonly Config.ModConfig _config;
        private readonly AnchorRing _ring;
        private readonly LiveStateStore _store;
        private readonly MetaRestore _meta = new MetaRestore();
        private PreRewindSnapshot _pre;
        private DeathSnapshot _death;
        private DamageInfo _capturedDamage;
        private string _backupPath;
        private const double PhaseTimeoutSeconds = 30.0;

        public WorldRestorer(Config.ModConfig config, AnchorRing ring, LiveStateStore store)
        { _config = config; _ring = ring; _store = store; }

        public void SetDeathContext(DeathSnapshot death, DamageInfo captured) { _death = death; _capturedDamage = captured; }

        public async Task<bool> RestoreAsync(Anchor anchor)
        {
            if (!await PhaseAAsync(anchor)) return false;
            if (!await PhaseBAsync(anchor)) { await PhaseFAsync(anchor); return false; }
            if (!await PhaseCAsync(anchor)) { await PhaseFAsync(anchor); return false; }
            if (!await PhaseDAsync(anchor)) { await PhaseFAsync(anchor); return false; }
            if (!await PhaseEAsync(anchor)) { await PhaseE6Async(); await PhaseFAsync(anchor); return false; }
            return true;
        }

        /// <summary>统一超时包装：超时只是"判定失败"的触发条件，返回 false 表示该阶段失败。</summary>
        private static async Task<bool> WithTimeout(Task<bool> work, double seconds, string phase)
        {
            var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds)));
            if (finished != work)
            {
                Debug.LogError($"[DeathRewind] restore phase={phase} 超时（>{seconds}s）");
                return false;
            }
            return await work;    // 必须等待终态：超时也不跳过在途链
        }

        private bool ForceFail(string phase) => _config.DebugMode && _config.ForceFailPhase == phase;
    }
}
```

- [ ] **Step 2: 实现各阶段（逐条对应规格 §8.1）**

`PhaseAAsync`：

```csharp
private async Task<bool> PhaseAAsync(Anchor anchor)
{
    try
    {
        if (ForceFail("A")) throw new InvalidOperationException("forced failure at A");
        // 1) 先刷新存档，使"回退前状态"= 磁盘 + 内存双侧一致态
        SavesSystem.CollectSaveData();
        SavesSystem.SaveFile();
        // 2) 备份主存档（路径留作字段，供阶段 B 失败与阶段 F 回滚）
        _backupPath = _meta.BackupCurrent();
        // 3) 采集回退前世界快照
        _pre = CapturePreRewindSnapshot();
        DumpPreState();
        // 4) 校验锚点载荷可解析
        foreach (var c in anchor.Containers.Values)
            if (c?.ItemPayload != null && InventoryPayload.Parse(c.ItemPayload) == null) return false;
        Log(RestorePhase.A, "ok");
        return true;
    }
    catch (Exception e) { Debug.LogError($"[DeathRewind] restore phase=A result=failed: {e.Message}"); return false; }
}
```

`CapturePreRewindSnapshot()`：世界时间（`TimeOfDayController.Time`）、玩家位置/旋转与背包载荷、每个容器的 `InventoryData` 副本、每扇门的 `closed`、每个敌人的实时态。

`DumpPreState()`：`logVerbose` 时输出 `phase=PRE` 逐实体明细（规格 §11.3）。

`PhaseBAsync`：`_meta.OverwriteFrom(anchor.MainSaveCopyPath)`；失败 → 用 `_backupPath` 回滚（磁盘回滚）+ 再次 `SavesSystem.SetFile`；二次失败仍先完成磁盘回滚。

`PhaseCAsync`：**先整体校验**所有容器载荷可解析，再逐个 `InventoryPayload.ApplyAsync`，每个都用 `WithTimeout` 包装并**等待终态**；任一失败 → 等待在途链静默后转阶段 F。

`PhaseDAsync`：按 §5.5 正向差量表恢复敌人/门/世界时间；子弹：销毁场上所有 `Projectile`；"移除"的敌人调用 `EntitySampler.MarkDeferredDestroy(id)`（只停用，销毁推迟到提交成功后）。

`PhaseEAsync`（E1–E6）：

```csharp
private async Task<bool> PhaseEAsync(Anchor anchor)
{
    // E1 复核 DeathSnapshot
    // E2 传送 + SetHealth + 背包原地还原（失败即用 _pre.PlayerBagPayload 回灌）
    // E3 复活标志：hasHandledDeath=false、Health.isDead=false、SetInvincible(false)、
    //     gameObject.SetActive(true)、LevelManager.dieTask=false；
    //     读 RaidUtilities.CurrentRaid，若 dead 仍为 true → 修正副本并 SavesSystem.Save("RaidInfo", 修正值)
    // E4 校验：Health.CurrentHealth > 0 && !Health.IsDead
    // E5 失败 → E6
}
```

`PhaseE6Async()`：**以 `DeathSnapshot` 为唯一权威来源**把 E2/E3 改动的字段逐项改回（血量、`isDead`、`hasHandledDeath`、`activeSelf`、`invincible`、位置/旋转、背包、`RaidInfo.dead`、`dieTask`）；复位失败 → 用 `_capturedDamage` 调 `Health.Hurt(...)` 强制死亡兜底。

`PhaseFAsync()`：磁盘回滚（`_backupPath` → 主存档 → `SetFile`）+ 按 §5.5 **反向差量表**把容器/门/敌人/世界时间/玩家位置整体还原为回退前状态（`B 活 / X 无` → 重新激活；对象确已销毁 → 无法复活，记入未覆盖清单）。

- [ ] **Step 3: 输出规格 §11.3 要求的日志**

统一由 `Log(phase, result)` 与若干专用方法输出，字段名必须与规格一致，否则 Task 15 的验证无从对照：

```
[DeathRewind] restore phase=<A|B|C|D|E|E2|E3|E4|E5|E6|F> result=<ok|failed|compensated>
[DeathRewind] restore player hp=<值> pos=<x,y,z> bagItems=<数> raidDead=false
[DeathRewind] world diff enemies=<变化数> doorsChanged=<数> containersChanged=<数> timeRestored=<值> worldState=<anchor|before>
[DeathRewind] meta exp=<值> quests=<键值摘要>
[DeathRewind] uncovered=<逗号分隔的未覆盖项>
```

回退**成功后**额外输出一行 `phase=POST` 的逐实体明细（与 `phase=PRE` 对照用）。

`exp`/`quests` 从存档键读取；若键名不确定，实施时先在游戏内打印相关键名再固定。

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 报错仅涉及 `DeathInterceptor`、`EnemyPersistencePatch`。

- [ ] **Step 5: 提交**

```bash
git add src/DeathRewind/Unity/WorldRestorer.cs
git commit -m "feat(unity): 分阶段恢复 A-F、差量表应用与验证日志"
```

---

## Chunk 3: Harmony 拦截、反射兼容、UI 与游戏内验证

### Task 11: HarmonyGuard（版本冲突检测）

**Files:**
- Create: `src/DeathRewind/Harmony/HarmonyGuard.cs`

- [ ] **Step 1: 实现**

```csharp
using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>补丁注册前的 Harmony 冲突检测（规格 §10）。</summary>
    public static class HarmonyGuard
    {
        /// <summary>本 Mod 期望的 Harmony 版本（与 vendor/0Harmony.dll 一致）。</summary>
        public static readonly Version ExpectedVersion = new Version(2, 4, 1, 0);

        /// <summary>返回 true 表示可以注册补丁；false 表示检测到冲突，应整体降级为"不拦截"。</summary>
        public static bool CanRegister(string tag)
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "0Harmony")
                    .ToList();
                if (loaded.Count == 0) return true;

                // 与"期望版本"比较，而不是与 typeof(Harmony) 绑定到的那份比较
                // （若别的 Mod 先加载了不同版本，类型绑定会解析到那一份，导致检测不到冲突）
                foreach (var asm in loaded)
                {
                    var v = asm.GetName().Version;
                    if (v == null || v.Major != ExpectedVersion.Major || v.Minor != ExpectedVersion.Minor)
                    {
                        Debug.LogError($"[DeathRewind] Harmony 版本冲突（发现 {v}，期望 {ExpectedVersion}），补丁 {tag} 未注册，Mod 降级为不拦截。");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] Harmony 冲突检测异常（{tag}），按冲突处理：{e.Message}");
                return false;
            }
        }
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add src/DeathRewind/Harmony/HarmonyGuard.cs
git commit -m "feat(harmony): 补丁注册前的版本冲突检测"
```

---

### Task 12: PrivateStateAccessor（集中反射访问私有字段）

**Files:**
- Create: `src/DeathRewind/Compat/PrivateStateAccessor.cs`

- [ ] **Step 1: 实现**

```csharp
using System;
using System.Reflection;
using UnityEngine;

namespace DeathRewind.Compat
{
    /// <summary>
    /// 集中反射访问游戏私有字段（LevelManager.dieTask、CharacterMainControl.hasHandledDeath、
    /// Health.isDead、TimeOfDayController.time）。字段失效时统一报错，不散落异常。
    /// </summary>
    public static class PrivateStateAccessor
    {
        private static bool _warned;

        private static FieldInfo FindField(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();
            while (t != null)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        public static bool GetBool(object target, string name)
        {
            var f = FindField(target, name);
            if (f == null)
            {
                WarnMissing(target, name);
                return false;
            }
            try { return (bool)f.GetValue(target); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 读取私有字段 {name} 失败: {e.Message}"); return false; }
        }

        public static void SetBool(object target, string name, bool value)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissing(target, name); return; }
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 写入私有字段 {name} 失败: {e.Message}"); }
        }

        public static float GetFloat(object target, string name)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissing(target, name); return 0f; }
            try { return (float)f.GetValue(target); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 读取私有字段 {name} 失败: {e.Message}"); return 0f; }
        }

        public static void SetFloat(object target, string name, float value)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissing(target, name); return; }
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 写入私有字段 {name} 失败: {e.Message}"); }
        }

        private static void WarnMissing(object target, string name)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogError($"[DeathRewind] 找不到私有字段 {name}（{target?.GetType().Name}），相关功能将降级；游戏更新后请同步本 Mod。");
        }
    }
}
```

> 用到的私有字段（已核实）：`LevelManager.dieTask`（bool）、`CharacterMainControl.hasHandledDeath`（bool）、`Health.isDead`（bool）、`TimeOfDayController.time`（float，仅 getter）。
> `Health.isDead` 的写入必须走这里（规格 §8.1 阶段 E3/E6）。

- [ ] **Step 2: 提交**

```bash
git add src/DeathRewind/Compat/PrivateStateAccessor.cs
git commit -m "feat(compat): 私有字段反射访问集中封装"
```

---

### Task 13: DeathInterceptor（拦截、冻结、裁决、放行与重放）

**Files:**
- Create: `src/DeathRewind/Harmony/DeathInterceptor.cs`
- Create: `src/DeathRewind/UI/RewindPrompt.cs`

- [ ] **Step 1: 实现拦截器**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DeathRewind.Core;
using DeathRewind.UI;
using DeathRewind.Unity;
using HarmonyLib;
using UnityEngine;

namespace DeathRewind.Harmony
{
    public static class DeathInterceptor
    {
        private static ModBehaviour _owner;
        private static Config.ModConfig _config;
        private static WorldRestorer _restorer;
        private static bool _interceptionActive;
        private static bool _bypassNext;          // 仅用于"下一帧重放"路径
        private static DamageInfo _capturedDamage;
        private static DeathSnapshot _deathSnapshot;
        private static Harmony _harmony;

        public static void Install(ModBehaviour owner, Config.ModConfig config, WorldRestorer restorer)
        {
            _owner = owner; _config = config; _restorer = restorer;
            if (!HarmonyGuard.CanRegister("com.deathrewind.interceptor"))
            {
                Debug.LogWarning("[DeathRewind] 因 Harmony 冲突，拦截功能未启用");
                return;
            }
            var target = AccessTools.Method(typeof(LevelManager), "OnMainCharacterDie", new[] { typeof(DamageInfo) });
            if (target == null)
            {
                Debug.LogError("[DeathRewind] 未找到 LevelManager.OnMainCharacterDie，拦截不可用");
                return;
            }
            _harmony = new Harmony("com.deathrewind.interceptor");
            _harmony.Patch(target, prefix: new HarmonyMethod(Prefix));
        }

        public static void Uninstall()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            _interceptionActive = false;
            _bypassNext = false;
            RewindPrompt.Close();
        }

        /// <summary>前缀必须是同步方法。用 __0 取原始形参，避免依赖原方法形参名。</summary>
        private static bool Prefix(DamageInfo __0)
        {
            if (_config == null || !_config.Enabled) return true;

            // 路径一：下一帧重放（"继续"或回退失败）——放行这一次
            if (_bypassNext)
            {
                _bypassNext = false;
                return true;
            }

            // 路径二：冻结期间的二次死亡——取消待定回退并放行当前这次（**不重放**、不再设 _bypassNext）
            if (_interceptionActive)
            {
                CancelPending();
                return true;
            }

            // 准入判定（规格 §6.4）：三条全部满足才拦截。AutoRewind 只影响是否显示面板。
            var ring = _owner.Ring;
            if (ring == null) return true;
            if (!ring.CanRewind())
            {
                Debug.Log(ring.RewindCount >= _config.MaxRewinds
                    ? "[DeathRewind] rewind limit reached"
                    : "[DeathRewind] no eligible anchor");
                return true;
            }

            _interceptionActive = true;
            _capturedDamage = __0;
            _deathSnapshot = CaptureDeathSnapshot();
            _restorer.SetDeathContext(_deathSnapshot, _capturedDamage);
            _owner.FreezeTimeScale();
            Debug.Log($"[DeathRewind] intercept at gridTime={_owner.CurrentTick} finalDamage={_capturedDamage.finalDamage}");

            if (_config.AutoRewind)
            {
                Submit(true);
            }
            else
            {
                RewindPrompt.Open(Submit);
            }
            return false;   // 吞掉原方法
        }

        /// <summary>面板裁决：true = 回退，false = 接受死亡。</summary>
        private static void Submit(bool rewind)
        {
            if (!_interceptionActive) return;
            try
            {
                if (rewind)
                {
                    var anchor = _owner.Ring.SelectTarget();
                    if (anchor == null)
                    {
                        Debug.Log("[DeathRewind] no eligible anchor");
                        _interceptionActive = false;
                        _owner.ReleaseTimeScale();
                        ScheduleReplay();
                        return;
                    }
                    Debug.Log($"[DeathRewind] anchor selected id={anchor.Id} anchorTs={anchor.AbsTimestamp} age={_owner.CurrentTick - anchor.AbsTimestamp} " +
                              $"anchorHp={anchor.Player?.Health} anchorPos={Format(anchor.Player?.Position)} anchorBagItems={CountItems(anchor.Player?.BagPayload)}");
                    _owner.StartCoroutine(RestoreRoutine(anchor));
                }
                else
                {
                    Debug.Log("[DeathRewind] player chose to accept death");
                    // 这条路径不会进入 RestoreRoutine，必须在这里自行收口：
                    // 解除冻结 + 清拦截标志，否则游戏会永久停在 0 倍速
                    _interceptionActive = false;
                    _owner.ReleaseTimeScale();
                    ScheduleReplay();
                }
            }
            finally
            {
                RewindPrompt.Close();
            }
        }

        private static IEnumerator RestoreRoutine(Anchor anchor)
        {
            bool ok = false;
            try
            {
                var task = _restorer.RestoreAsync(anchor);
                while (!task.IsCompleted) yield return null;
                ok = task.Result;      // 阶段内部已 try/catch；此处仍需 try 防止 AggregateException
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] 回退异常: {e}");
                ok = false;
            }

            if (ok)
            {
                var removed = new List<Anchor>();
                _owner.Ring.CommitRewind(anchor, removed);
                _owner.Ring.ResetGridTo(anchor.GridTimestamp, _owner.CurrentTick);
                foreach (var old in removed) _owner.ReleaseAnchor(old);
                _owner.ReleaseCommittedDestroy();
                Debug.Log($"[DeathRewind] rewind result=ok rewindCount={_owner.Ring.RewindCount} consumedAnchor={anchor.Id}");
            }
            else
            {
                Debug.LogWarning($"[DeathRewind] rewind result=failed rewindCount={_owner.Ring.RewindCount}");
                ScheduleReplay();     // 回退失败 → 放行死亡流程，避免卡死
            }

            _interceptionActive = false;
            _owner.ReleaseTimeScale();
            RewindPrompt.Close();
        }

        /// <summary>下一帧反射调用原方法，复用捕获的原始 DamageInfo。</summary>
        private static void ScheduleReplay()
        {
            _bypassNext = true;
            var target = AccessTools.Method(typeof(LevelManager), "OnMainCharacterDie", new[] { typeof(DamageInfo) });
            var damage = _capturedDamage;
            _owner.StartCoroutine(ReplayNextFrame(target, damage));
        }

        private static IEnumerator ReplayNextFrame(MethodInfo target, DamageInfo damage)
        {
            yield return null;   // 下一帧
            if (target == null) return;
            try { target.Invoke(LevelManager.Instance, new object[] { damage }); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 重放死亡流程失败: {e}"); }
        }

        private static void CancelPending()
        {
            RewindPrompt.Close();
            _owner?.ReleaseTimeScale();
            _interceptionActive = false;      // 必须清：否则本关卡后续每次死亡都走"二次进入"分支
        }

        /// <summary>拦截时刻直接读取的玩家死亡态（反向复位的唯一权威来源）。</summary>
        private static DeathSnapshot CaptureDeathSnapshot()
        {
            var player = LevelManager.Instance?.MainCharacter;
            var snap = new DeathSnapshot();
            if (player == null) return snap;
            var health = player.Health;
            snap.Health = health.CurrentHealth;
            snap.IsDead = health.IsDead;
            snap.HandledDeath = Compat.PrivateStateAccessor.GetBool(player, "hasHandledDeath");
            snap.ActiveSelf = player.gameObject.activeSelf;
            snap.Invincible = health.Invincible;
            snap.RaidDead = RaidUtilities.CurrentRaid.dead;
            snap.Position = new[] { player.transform.position.x, player.transform.position.y, player.transform.position.z };
            snap.Rotation = new[] { player.transform.rotation.x, player.transform.rotation.y, player.transform.rotation.z, player.transform.rotation.w };
            snap.BagPayload = InventoryPayload.Capture(player.CharacterItem.Inventory);
            return snap;
        }

        private static string Format(float[] v) => v == null ? "-" : $"{v[0]:0.0},{v[1]:0.0},{v[2]:0.0}";
        private static int CountItems(byte[] payload)
        {
            var data = InventoryPayload.Parse(payload);
            return data?.entries?.Count ?? 0;
        }
    }
}
```

要点：

- 前置补丁**必须同步**（返回 `bool`）。
- **准入判定始终执行**：`AutoRewind` 只影响"是否显示面板"。
- 二次进入路径**不设** `_bypassNext`：当前这次由 `return true` 放行即可。
- 重放路径需要 `_bypassNext`：Harmony 的 `MethodInfo.Invoke` 会再次进入 detour 前缀，重放时必须让前缀放行一次。
- 回退失败必须走 `ScheduleReplay()`，否则玩家已死但 `CharacterDieTask` 永不触发（死亡流程卡死）。
- `RestoreRoutine` 用 try/catch 包住 `task.Result`，并在末尾**无条件**复位 `_interceptionActive` / `timeScale` / 面板。

- [ ] **Step 2: 实现 IMGUI 面板**

`src/DeathRewind/UI/RewindPrompt.cs`：

```csharp
using System;
using UnityEngine;

namespace DeathRewind.UI
{
    /// <summary>冻结期间绘制的确认面板。屏幕绝对坐标绘制，不挂到玩家对象上。</summary>
    public static class RewindPrompt
    {
        private static Action<bool> _callback;
        private static bool _submitted;

        public static void Open(Action<bool> callback) { _callback = callback; _submitted = false; }
        public static void Close() { _callback = null; _submitted = false; }
        public static bool IsOpen => _callback != null && !_submitted;

        public static void Submit(bool rewind)
        {
            if (_callback == null || _submitted) return;
            _submitted = true;
            var cb = _callback;
            _callback = null;
            cb(rewind);
        }

        /// <summary>只能在 Unity 的 OnGUI 回调中调用。</summary>
        public static void Draw()
        {
            if (_callback == null || _submitted) return;
            float w = 460f, h = 180f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("被击败了");
            GUILayout.Label("回退到约 120–180 秒前的世界状态？");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("继续（接受死亡）")) Submit(false);
            if (GUILayout.Button("回退")) Submit(true);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 报错仅涉及 `EnemyPersistencePatch`。

- [ ] **Step 4: 提交**

```bash
git add src/DeathRewind/Harmony/DeathInterceptor.cs src/DeathRewind/UI/RewindPrompt.cs
git commit -m "feat(harmony): 死亡拦截、裁决与确认面板"
```

---

### Task 14: EnemyPersistencePatch（接管敌人销毁）

**Files:**
- Create: `src/DeathRewind/Harmony/EnemyPersistencePatch.cs`

- [ ] **Step 1: 实现**

```csharp
using DeathRewind.Unity;
using HarmonyLib;
using UnityEngine;

namespace DeathRewind.Harmony
{
    public static class EnemyPersistencePatch
    {
        private static EntitySampler _sampler;
        private static Harmony _harmony;

        public static void Install(EntitySampler sampler)
        {
            _sampler = sampler;
            if (!HarmonyGuard.CanRegister("com.deathrewind.enemypersistence"))
            {
                Debug.LogWarning("[DeathRewind] 因 Harmony 冲突，敌人持久化补丁未启用");
                return;
            }
            var target = AccessTools.Method(typeof(Health), "DestroyOnDelay");
            if (target == null) { Debug.LogError("[DeathRewind] 未找到 Health.DestroyOnDelay"); return; }
            _harmony = new Harmony("com.deathrewind.enemypersistence");
            _harmony.Patch(target, prefix: new HarmonyMethod(Prefix));
        }

        public static void Uninstall() { _harmony?.UnpatchSelf(); _harmony = null; }

        /// <summary>
        /// 前缀必须是同步方法；返回 false 即整条 async 状态机被跳过（调用方 await 到 default(UniTask) 立即完成）。
        /// 只接管"敌人角色"，非敌人的 Health（如可破坏物）照常销毁。
        /// </summary>
        private static bool Prefix(Health __instance)
        {
            if (__instance == null) return true;
            if (!EntitySampler.IsEnemyCharacter(__instance)) return true;
            _sampler?.RegisterDeadEnemy(__instance);
            return false;
        }
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/DeathRewind/DeathRewind.csproj -c Release`
Expected: 编译成功，0 error。

- [ ] **Step 3: 提交**

```bash
git add src/DeathRewind/Harmony/EnemyPersistencePatch.cs
git commit -m "feat(harmony): 接管敌人销毁以支持复活"
```

---

### Task 15: 部署与加载验证

**Files:**
- Modify: `tools/build.sh`（部署后打印最终路径与文件清单）

- [ ] **Step 1: 部署**

Run: `bash tools/build.sh`
Expected: 输出 `OK: Core purity`、构建成功、`deployed to .../Contents/Mods/DeathRewind`，并列出 `DeathRewind.dll`、`info.ini`、`preview.png`、`0Harmony.dll`（恰好一份）。

- [ ] **Step 2: 确认游戏能发现该 Mod**

启动游戏 → 主菜单 → Mods 菜单：
- 应能看到"死亡回档"（`displayName`）。
- 点击界面上的"复制路径"按钮，与 `tools/build.sh` 打印的 `MODS_DIR` 比对；不一致则以游戏给出的路径为准（这是 `Application.dataPath/Mods` 的权威值）。
- 若未出现：检查 `info.ini` 是否含 `name`（缺失会被 Abort）、DLL 名是否为 `DeathRewind.dll`、`preview.png` 是否存在。

- [ ] **Step 3: 确认加载日志**

启用该 Mod 并进入游戏 → 读 `~/Library/Logs/Company/TeamSoda/Duckov/Player.log`：
- 出现 `[DeathRewind] mods root: <路径>`（打印 `ModManager.DefaultModFolderPath`）与 `[DeathRewind] initialized`；
- 不得出现 `Could not load file or assembly 0Harmony`；若出现，说明与其他 Mod 的 Harmony 冲突，`HarmonyGuard` 只能事后告警——按规格 §10 记录为已知限制。

- [ ] **Step 4: 提交**

```bash
git add tools/build.sh
git commit -m "chore: 部署脚本输出最终路径与文件清单"
```

---

### Task 16: 游戏内行为验证（对照规格 §11.3）

**Files:**
- Create: `docs/superpowers/plans/2026-10-20-death-rewind-verification.md`
- Create: `docs/superpowers/plans/config.example.txt`（配置样例，实际配置文件在游戏目录内）

- [ ] **Step 1: 准备配置**

仓库内样例 `docs/superpowers/plans/config.example.txt`：

```
enabled=true
debugMode=true
logVerbose=true
forceFailPhase=
```

实际生效路径：`Contents/ModConfig/DeathRewind/config.txt`（内容同上）。**配置在 `OnAfterSetup` 一次性读取**，每轮修改后必须**重新加载 Mod**（Mods 菜单里停用再启用，或重启游戏）才会生效。

- [ ] **Step 2: 逐条执行验证清单**

按规格 §11.3 的 8 条执行，每条都要有日志证据：

| # | 步骤 | 期望日志 |
|---|---|---|
| 1 | 战斗中按 F10 致死 | `[DeathRewind] intercept at gridTime=… finalDamage=…`；`timeScale == 0`；面板可见（截图） |
| 2 | 按 F9 回退 | `anchor selected id=… anchorTs=… age=… anchorHp=… anchorPos=… anchorBagItems=…`；`restore phase=E result=ok`；`restore player hp=… pos=… bagItems=…`；`world diff … worldState=anchor`；`rewind result=ok rewindCount=1 consumedAnchor=…` |
| 3 | 立刻再次致死并回退 | 第二次 `anchor selected` 的 `id` 与第 2 步不同、`age` 更大（前置：关卡已运行 ≥ 240 s） |
| 4 | 同一窗口内第三次致死 | `no eligible anchor` 或 `rewind limit reached`；游戏走正常失败场景 |
| 5 | 依次把 `forceFailPhase` 设为 `A`/`B`/`C`/`D`/`E`，**每轮改完配置 → 重新加载 Mod → 死一次 → 回退** | `restore phase=<该阶段> result=failed`（B 之后追加 `restore phase=F result=compensated`）；玩家仍死亡（血量 == `DeathSnapshot` 的 hp）；`meta exp=…` 与回退前一致；`rewind result=failed` 且 `rewindCount` 未递增 |
| 6 | 回退前后逐实体比对 | `logVerbose` 的 `phase=PRE`/`phase=POST` 明细逐项一致 |
| 7 | 存档一致性 | `Save_{slot}.sav` 的 EXP 与任务键回到锚点值，无重复领奖 |
| 8 | 进入子场景 | `subscene loaded: anchors=… rewindCount=…` —— 锚点环未清空、`rewindCount` 未归零 |

- [ ] **Step 3: 记录验证结论**

在 `docs/superpowers/plans/2026-10-20-death-rewind-verification.md` 写入每条的实际日志片段与结论。

- [ ] **Step 4: 提交**

```bash
git add docs/superpowers/plans/2026-10-20-death-rewind-verification.md docs/superpowers/plans/config.example.txt
git commit -m "test: 游戏内行为验证（对照规格 11.3 清单）"
```

---

## 完成定义

- `dotnet test tests/DeathRewind.Tests/` 全绿；`bash tools/check-core-purity.sh` 通过。
- `bash tools/build.sh` 成功部署，Mods 目录中恰有一份 `0Harmony.dll`。
- 游戏内 Mods 菜单可见并能加载，日志出现 `[DeathRewind] initialized`。
- 规格 §11.3 的 8 条验证全部有日志证据并通过。

