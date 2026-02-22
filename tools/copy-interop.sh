#!/usr/bin/env bash
# Copies BepInEx-generated interop DLLs from the game directory to the project.
# Run after BepInEx has been installed and the game has been launched once.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
GAME_DIR="${GAME_DIR:-/mnt/c/Program Files (x86)/Steam/steamapps/common/Super Auto Pets}"
INTEROP_SRC="$GAME_DIR/BepInEx/interop"
INTEROP_DST="$PROJECT_ROOT/interop"

if [ ! -d "$INTEROP_SRC" ]; then
    echo "ERROR: Interop directory not found: $INTEROP_SRC"
    echo "Make sure BepInEx is installed and you've launched the game once."
    exit 1
fi

echo "Copying interop DLLs..."
rm -rf "$INTEROP_DST"
mkdir -p "$INTEROP_DST"
cp "$INTEROP_SRC"/*.dll "$INTEROP_DST/"

COUNT=$(ls -1 "$INTEROP_DST"/*.dll 2>/dev/null | wc -l)
echo "Copied $COUNT interop DLLs to $INTEROP_DST"
