#!/usr/bin/env bash
# Core/ 只允许引用 .NET 基类库：禁止 Unity / 游戏程序集 / EasySave3
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORE="$ROOT/src/DeathRewind/Core"
if [ ! -d "$CORE" ]; then
  echo "OK: Core purity (no Core dir)"
  exit 0
fi
fail=0
if grep -RInE 'using[[:space:]]+(UnityEngine|Duckov|Saves|ItemStatsSystem|TeamSoda|EasySave3|Cysharp)' "$CORE"; then
  echo "FAIL: Core/ 出现了禁止的 using" >&2; fail=1
fi
if grep -RInE '\b(UnityEngine|ItemStatsSystem|EasySave3|Duckov|Saves|TeamSoda|Cysharp)\.' "$CORE"; then
  echo "FAIL: Core/ 出现了禁止的全限定类型引用" >&2; fail=1
fi
[ "$fail" -eq 0 ] && echo "OK: Core purity"
exit "$fail"
