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
