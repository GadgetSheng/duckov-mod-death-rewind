#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${GAME_DIR:-/Users/gadget/Library/Application Support/Steam/steamapps/common/Escape from Duckov}"
MANAGED_DIR="$GAME_DIR/Duckov.app/Contents/Resources/Data/Managed"
MODS_DIR="${MODS_DIR:-$GAME_DIR/Duckov.app/Contents/Mods/DeathRewind}"   # macOS 实测：ModManager 扫描的是 Contents/Mods

bash "$ROOT/tools/fetch-harmony.sh"
bash "$ROOT/tools/check-core-purity.sh"

dotnet build "$ROOT/src/DeathRewind/DeathRewind.csproj" -c Release -p:DuckovManagedDir="$MANAGED_DIR"

OUT="$ROOT/src/DeathRewind/bin/Release"
mkdir -p "$MODS_DIR"
cp "$OUT/DeathRewind.dll" "$MODS_DIR/"
cp "$ROOT/src/DeathRewind/info.ini" "$MODS_DIR/"
cp "$ROOT/src/DeathRewind/preview.png" "$MODS_DIR/"
cp "$ROOT/vendor/0Harmony.dll" "$MODS_DIR/"
echo "deployed to $MODS_DIR"
ls -la "$MODS_DIR"
