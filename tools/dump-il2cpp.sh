#!/usr/bin/env bash
# Runs Il2CppDumper on the game's GameAssembly.dll and global-metadata.dat.
# Requires Il2CppDumper to be available (downloaded separately).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
GAME_DIR="${GAME_DIR:-$PROJECT_ROOT/Super Auto Pets}"
DUMPS_DIR="$PROJECT_ROOT/dumps"

GAME_ASSEMBLY="$GAME_DIR/GameAssembly.dll"
METADATA="$GAME_DIR/Super Auto Pets_Data/il2cpp_data/Metadata/global-metadata.dat"

# Il2CppDumper path - adjust as needed
IL2CPP_DUMPER="${IL2CPP_DUMPER:-$PROJECT_ROOT/tools/Il2CppDumper/Il2CppDumper}"

if [ ! -f "$GAME_ASSEMBLY" ]; then
    echo "ERROR: GameAssembly.dll not found at: $GAME_ASSEMBLY"
    exit 1
fi

if [ ! -f "$METADATA" ]; then
    echo "ERROR: global-metadata.dat not found at: $METADATA"
    exit 1
fi

if [ ! -f "$IL2CPP_DUMPER" ]; then
    echo "ERROR: Il2CppDumper not found at: $IL2CPP_DUMPER"
    echo "Download from: https://github.com/Perfare/Il2CppDumper/releases"
    echo "Set IL2CPP_DUMPER env var to point to the executable."
    exit 1
fi

mkdir -p "$DUMPS_DIR"

echo "Running Il2CppDumper..."
"$IL2CPP_DUMPER" "$GAME_ASSEMBLY" "$METADATA" "$DUMPS_DIR"

echo "Dump output saved to: $DUMPS_DIR"
echo "Key files: dump.cs, il2cpp.h, script.json"
