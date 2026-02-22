#!/usr/bin/env bash
# Builds and deploys SAPAccess mod to the game directory.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
GAME_DIR="${GAME_DIR:-/mnt/c/Program Files (x86)/Steam/steamapps/common/Super Auto Pets}"
PLUGIN_DIR="$GAME_DIR/BepInEx/plugins/SAPAccess"

CSPROJ="$PROJECT_ROOT/src/SAPAccess/SAPAccess.csproj"
NVDA_DLL="$PROJECT_ROOT/lib/nvdaControllerClient64.dll"

echo "Building SAPAccess..."
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build "$CSPROJ" -c Release

BUILD_OUTPUT="$PROJECT_ROOT/src/SAPAccess/bin/Release/net6.0/SAPAccess.dll"
if [ ! -f "$BUILD_OUTPUT" ]; then
    echo "ERROR: Build output not found: $BUILD_OUTPUT"
    exit 1
fi

echo "Deploying to game directory..."
mkdir -p "$PLUGIN_DIR"
cp "$BUILD_OUTPUT" "$PLUGIN_DIR/"
echo "  -> $PLUGIN_DIR/SAPAccess.dll"

if [ -f "$NVDA_DLL" ]; then
    cp "$NVDA_DLL" "$GAME_DIR/"
    echo "  -> $GAME_DIR/nvdaControllerClient64.dll"
else
    echo "WARNING: nvdaControllerClient64.dll not found in lib/"
fi

echo ""
echo "Deployment complete. Launch the game to test."
echo "Check $GAME_DIR/BepInEx/LogOutput.log for output."
