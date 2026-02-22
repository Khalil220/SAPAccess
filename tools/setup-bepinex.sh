#!/usr/bin/env bash
# Downloads and installs BepInEx 6 (IL2CPP) to the Super Auto Pets game directory.
# Run from WSL. Uses python3 zipfile if unzip is unavailable.
set -euo pipefail

BEPINEX_VERSION="6.0.0-be.753"
BEPINEX_BUILD="753"
BEPINEX_COMMIT="0d275a4"
BEPINEX_URL="https://builds.bepinex.dev/projects/bepinex_be/${BEPINEX_BUILD}/BepInEx-Unity.IL2CPP-win-x64-${BEPINEX_VERSION}%2B${BEPINEX_COMMIT}.zip"
GAME_DIR="${GAME_DIR:-/mnt/c/Program Files (x86)/Steam/steamapps/common/Super Auto Pets}"

if [ ! -d "$GAME_DIR" ]; then
    echo "ERROR: Game directory not found: $GAME_DIR"
    echo "Set GAME_DIR environment variable to your game install path."
    exit 1
fi

TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

echo "Downloading BepInEx ${BEPINEX_VERSION}..."
wget -q "$BEPINEX_URL" -O "$TEMP_DIR/bepinex.zip"

echo "Extracting to game directory..."
if command -v unzip &>/dev/null; then
    unzip -o "$TEMP_DIR/bepinex.zip" -d "$GAME_DIR"
else
    python3 -c "
import zipfile, sys
with zipfile.ZipFile(sys.argv[1], 'r') as z:
    z.extractall(sys.argv[2])
" "$TEMP_DIR/bepinex.zip" "$GAME_DIR"
fi

echo "BepInEx installed to: $GAME_DIR"
echo ""
echo "Next steps:"
echo "  1. Launch Super Auto Pets once via Steam to generate interop assemblies"
echo "  2. Run ./tools/copy-interop.sh to copy interop DLLs to this project"
