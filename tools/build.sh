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

# —— workshop 目录同步：默认关闭 ——
# 游戏加载顺序是 workshop 优先 → Contents/Mods 其次，
# 但发布走游戏内 Workshop 上传界面，Steam 会自动拉最新版。
# 只有需要立即让本地副本生效时才手动开启：
#     WORKSHOP_DEPLOY=1 bash tools/build.sh
# 精确匹配 publishedFileId，避免 find head -1 误伤其他 Mod 目录。
if [ "${WORKSHOP_DEPLOY:-0}" = "1" ]; then
    if [ -n "$WORKSHOP_DIR" ] && [ -d "$WORKSHOP_DIR" ]; then
        deploy_to "$WORKSHOP_DIR"
        echo "✅ workshop 同步完成（${WORKSHOP_DIR:-}）"
    else
        echo "❌ WORKSHOP_DEPLOY=1 但 WORKSHOP_DIR 未找到，publishedFileId=$PUBLISHED_ID" >&2
        exit 1
    fi
else
    echo "（workshop 同步已关闭，需要立即生效时加 WORKSHOP_DEPLOY=1）"
fi
