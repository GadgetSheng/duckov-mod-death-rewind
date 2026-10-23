#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${GAME_DIR:-/Users/gadget/Library/Application Support/Steam/steamapps/common/Escape from Duckov}"
MANAGED_DIR="$GAME_DIR/Duckov.app/Contents/Resources/Data/Managed"
MODS_DIR="${MODS_DIR:-$GAME_DIR/Duckov.app/Contents/Mods/DeathRewind}"   # macOS 实测：ModManager 扫描的是 Contents/Mods
WORKSHOP_ROOT="$HOME/Library/Application Support/Steam/steamapps/workshop/content/3167020"
# 优先从 info.ini 的 publishedFileId 读取精确 ID，避免 find head -1 命中其他 Mod 目录
PUBLISHED_ID=$(grep -oE 'publishedFileId[[:space:]]*=[[:space:]]*[0-9]+' "$ROOT/src/DeathRewind/info.ini" | grep -oE '[0-9]+$' || true)
if [ -n "${WORKSHOP_DIR_OVERRIDE:-}" ]; then
    WORKSHOP_DIR="$WORKSHOP_DIR_OVERRIDE"
elif [ -n "$PUBLISHED_ID" ] && [ -d "$WORKSHOP_ROOT/$PUBLISHED_ID" ]; then
    WORKSHOP_DIR="$WORKSHOP_ROOT/$PUBLISHED_ID"
elif [ -d "$WORKSHOP_ROOT/3823953866" ]; then
    WORKSHOP_DIR="$WORKSHOP_ROOT/3823953866"
else
    WORKSHOP_DIR=""
fi

bash "$ROOT/tools/fetch-harmony.sh"
bash "$ROOT/tools/check-core-purity.sh"

dotnet build "$ROOT/src/DeathRewind/DeathRewind.csproj" -c Release -p:DuckovManagedDir="$MANAGED_DIR"

OUT="$ROOT/src/DeathRewind/bin/Release"

deploy_to() {
    local target="$1"
    mkdir -p "$target"
    cp "$OUT/DeathRewind.dll" "$target/"
    cp "$ROOT/src/DeathRewind/info.ini" "$target/"
    cp "$ROOT/src/DeathRewind/preview.png" "$target/"
    cp "$ROOT/vendor/0Harmony.dll" "$target/"
    echo "deployed → $target"
}

deploy_to "$MODS_DIR"

# 游戏加载顺序：workshop 优先 → Contents/Mods 其次，
# 所以 workshop 永远是实际生效的那份，构建后必须同步。
if [ -n "$WORKSHOP_DIR" ] && [ -d "$WORKSHOP_DIR" ]; then
    deploy_to "$WORKSHOP_DIR"
    echo "⚠️  游戏优先加载 workshop 目录，本次同步已覆盖生效路径"
else
    echo "⚠️  WORKSHOP_DIR 未找到，只部署到 Contents/Mods —— 请手动同步 workshop/content/3167020/<id>/ 下的 4 个文件"
fi
