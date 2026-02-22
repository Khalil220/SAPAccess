#!/usr/bin/env bash
# Downloads and installs BepInEx 6 (IL2CPP) to the Super Auto Pets game directory.
# Run from WSL. Requires unzip.
set -euo pipefail

BEPINEX_VERSION="6.0.0-be.753"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx-Unity.IL2CPP-win-x64-${BEPINEX_VERSION}.zip"
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
unzip -o "$TEMP_DIR/bepinex.zip" -d "$GAME_DIR"

echo "BepInEx installed to: $GAME_DIR"
echo ""
echo "Next steps:"
echo "  1. Launch Super Auto Pets once via Steam to generate interop assemblies"
echo "  2. Run ./tools/copy-interop.sh to copy interop DLLs to this project"
