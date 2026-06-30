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

cd "$STAGING"
rm -f "$MODS_DIR/$MOD_NAME.zip"
zip -r -q "$MODS_DIR/$MOD_NAME.zip" *

echo "=== Cleaning up ==="
rm -rf "$STAGING"

echo "=== Deployed to $MODS_DIR/$MOD_NAME.zip ==="
