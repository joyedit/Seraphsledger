#!/bin/bash
set -e

MOD_NAME="SeraphsLedger"
MODS_DIR="$HOME/.config/VintagestoryData/Mods"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=== Cleaning ==="
rm -rf "$SCRIPT_DIR/obj" "$SCRIPT_DIR/bin"

echo "=== Building ==="
dotnet build -c Debug "$SCRIPT_DIR/$MOD_NAME.csproj"

echo "=== Packaging ==="
STAGING="/tmp/${MOD_NAME}_stage"
rm -rf "$STAGING"
mkdir -p "$STAGING"

cp "$SCRIPT_DIR/bin/Debug/$MOD_NAME.dll" "$STAGING/"
cp "$SCRIPT_DIR/modinfo.json" "$STAGING/"
[ -f "$SCRIPT_DIR/modicon.png" ] && cp "$SCRIPT_DIR/modicon.png" "$STAGING/"
cp -r "$SCRIPT_DIR/assets" "$STAGING/"

# Zip is named with the mod version (SeraphsLedger_x.y.z.zip); older versions
# are removed so the game never loads two copies side by side.
VERSION=$(grep -oP '"version":\s*"\K[^"]+' "$SCRIPT_DIR/modinfo.json")
ZIP_NAME="${MOD_NAME}_${VERSION}.zip"

cd "$STAGING"
rm -f "$MODS_DIR/$MOD_NAME"*.zip
zip -r -q "$MODS_DIR/$ZIP_NAME" *

echo "=== Cleaning up ==="
rm -rf "$STAGING"

echo "=== Deployed to $MODS_DIR/$ZIP_NAME ==="
