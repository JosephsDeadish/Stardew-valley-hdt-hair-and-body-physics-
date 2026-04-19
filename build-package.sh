#!/usr/bin/env bash
# build-package.sh — Build SVP Physics and package it as an installable zip.
#
# Usage:
#   ./build-package.sh [--game-path /path/to/StardewValley] [--output-dir dist]
#
# If --game-path is omitted the script tries common Steam locations.
# On macOS Steam installs to ~/Library/Application Support/Steam/...
# On Linux it's typically ~/.local/share/Steam/steamapps/common/...
set -euo pipefail

MOD_FOLDER="SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MOD_DIR="$SCRIPT_DIR/mod/$MOD_FOLDER"
PROJECT_DIR="$SCRIPT_DIR/src/StardewHdtPhysics"
CSPROJ="$PROJECT_DIR/StardewHdtPhysics.csproj"
GAME_PATH=""
OUTPUT_DIR="$SCRIPT_DIR/dist"

# ── Parse arguments ───────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --game-path)  GAME_PATH="$2";  shift 2 ;;
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# ── Auto-detect game path ─────────────────────────────────────────────────────
if [[ -z "$GAME_PATH" ]]; then
    candidates=(
        "$HOME/.local/share/Steam/steamapps/common/Stardew Valley"
        "$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley"
        "/mnt/c/Program Files (x86)/Steam/steamapps/common/Stardew Valley"
        "/opt/stardewvalley"
    )
    for c in "${candidates[@]}"; do
        if [[ -f "$c/StardewValley" || -f "$c/StardewValley.exe" ]]; then
            GAME_PATH="$c"
            echo "Auto-detected game path: $GAME_PATH"
            break
        fi
    done
fi

# ── Build ─────────────────────────────────────────────────────────────────────
echo ""
echo "==> Building StardewHdtPhysics ..."

if [[ -n "$GAME_PATH" ]]; then
    dotnet build "$CSPROJ" -c Release "/p:GamePath=$GAME_PATH"
else
    echo "WARNING: Stardew Valley not found — building without auto-deploy."
    echo "         Pass --game-path /path/to/StardewValley to enable auto-deploy."
    dotnet build "$CSPROJ" -c Release /p:EnableGameDeployment=false
fi

# ── Stage mod folder ──────────────────────────────────────────────────────────
echo ""
echo "==> Staging mod folder ..."

DLL_SRC="$PROJECT_DIR/bin/Release/net6.0/StardewHdtPhysics.dll"
if [[ ! -f "$DLL_SRC" ]]; then
    echo "ERROR: DLL not found at: $DLL_SRC  (did the build succeed?)"
    exit 1
fi

# Copy DLL into the mod template folder so it's complete for drag-and-drop
cp "$DLL_SRC" "$MOD_DIR/StardewHdtPhysics.dll"
echo "  DLL  → $MOD_DIR/StardewHdtPhysics.dll"

# Sync assets and manifest from source
cp "$PROJECT_DIR/assets/"*.json "$MOD_DIR/assets/"
echo "  assets → $MOD_DIR/assets/"

cp "$PROJECT_DIR/manifest.json" "$MOD_DIR/"
echo "  manifest.json → $MOD_DIR/"

# ── Create distribution zip ───────────────────────────────────────────────────
echo ""
echo "==> Packaging zip ..."

mkdir -p "$OUTPUT_DIR"
ZIP_PATH="$OUTPUT_DIR/$MOD_FOLDER.zip"
rm -f "$ZIP_PATH"

# Use a subshell so the paths inside the zip are relative
(cd "$(dirname "$MOD_DIR")" && zip -r "$ZIP_PATH" "$MOD_FOLDER" -x "*.DS_Store")

echo ""
echo "====================================================="
echo " Package ready:"
echo "   $ZIP_PATH"
echo ""
echo " To install manually:"
echo "   unzip the archive"
echo "   copy '$MOD_FOLDER/' into Stardew Valley/Mods/"
echo "====================================================="
