#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/XelsCombatAI/XelsCombatAI.csproj"
OUT="$ROOT/artifacts"
PUBLISH="$OUT/publish"
ZIP="$OUT/XelsCombatAI.zip"

rm -rf "$OUT"
mkdir -p "$PUBLISH"

dotnet build "$PROJECT" -c Release -p:EnableWindowsTargeting=true

BUILD_DIR="$ROOT/XelsCombatAI/bin/Release"
if [[ ! -f "$BUILD_DIR/XelsCombatAI.dll" ]]; then
  BUILD_DIR="$ROOT/XelsCombatAI/bin/x64/Release"
fi

if [[ ! -f "$BUILD_DIR/XelsCombatAI.dll" ]]; then
  echo "error: built XelsCombatAI.dll not found under $ROOT/XelsCombatAI/bin" >&2
  exit 1
fi

cp "$BUILD_DIR/XelsCombatAI.dll" "$PUBLISH/"
cp "$BUILD_DIR/XelsCombatAI.deps.json" "$PUBLISH/" 2>/dev/null || true
cp "$BUILD_DIR/XelsCombatAI.json" "$PUBLISH/"
cp "$BUILD_DIR/ECommons.dll" "$PUBLISH/" 2>/dev/null || true
cp "$BUILD_DIR/icon.png" "$PUBLISH/" 2>/dev/null || true

(
  cd "$PUBLISH"
  zip -9 -r "$ZIP" .
)

echo "Wrote $ZIP"
