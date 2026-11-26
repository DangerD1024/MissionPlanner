#!/bin/bash
# Builds the macOS disk image of Mission Planner 3 (src/MissionPlanner.App): a self-contained
# publish (no .NET install needed) wrapped in "Mission Planner 3.app", signed ad hoc (Apple Silicon
# refuses to run unsigned code; this is not a Developer ID signature, so Gatekeeper still asks on
# first open), packed as bin/MissionPlanner-<version>-<rid>.dmg with a link to Applications.
# Run on a Mac with the .NET 10 SDK:
#   installer/macos/build-dmg.sh [osx-arm64|osx-x64]
# The version is MissionPlannerVersion in src/Directory.Build.props.
set -euo pipefail
rid="${1:-osx-arm64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"

version="$(sed -n 's:.*<MissionPlannerVersion>\(.*\)</MissionPlannerVersion>.*:\1:p' src/Directory.Build.props | head -1)"
[ -n "$version" ] || { echo "MissionPlannerVersion not found in src/Directory.Build.props" >&2; exit 1; }
numeric="$(echo "$version" | sed -E 's/^([0-9]+(\.[0-9]+){0,3}).*/\1/')"
echo "Mission Planner $version ($numeric), $rid"

stage="bin/dmg-$rid"
app="$stage/Mission Planner 3.app"
rm -rf "$stage"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish src/MissionPlanner.App -c Release -r "$rid" --self-contained true -o "$app/Contents/MacOS"
find "$app/Contents/MacOS" -name '*.pdb' -delete

# icon: every size the iconset wants, from the largest picture in the app's .ico (or the 128 px png)
iconset="$stage/AppIcon.iconset"
mkdir -p "$iconset"
src_icon="$stage/icon-source.png"
if ! sips -s format png "src/MissionPlanner.App/Assets/mpdesktop.ico" --out "$src_icon" >/dev/null 2>&1; then
    cp "src/MissionPlanner.App/Assets/mission-planner-128.png" "$src_icon"
fi
for size in 16 32 128 256 512; do
    sips -z $size $size "$src_icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$src_icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Mission Planner 3</string>
  <key>CFBundleDisplayName</key><string>Mission Planner 3</string>
  <key>CFBundleIdentifier</key><string>org.missionplanner.mp3</string>
  <key>CFBundleExecutable</key><string>MissionPlanner.App</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$numeric</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSCameraUsageDescription</key><string>Video from a USB capture device behind the HUD.</string>
  <key>NSMicrophoneUsageDescription</key><string>Audio from a USB capture device.</string>
  <key>NSLocalNetworkUsageDescription</key><string>MAVLink and video links to vehicles on the local network.</string>
  <key>NSBluetoothAlwaysUsageDescription</key><string>Bluetooth telemetry links.</string>
</dict>
</plist>
PLIST

chmod 755 "$app/Contents/MacOS/MissionPlanner.App"
# the bundled dfu-util (STM32 DFU flashing) and its libusb: executable, and signed one by one before the
# app (not left to --deep), so the bundle seal covers them as signed code
dfu="$app/Contents/MacOS/Tools/dfu-util"
[ -f "$dfu/dfu-util" ] || { echo "the bundled dfu-util is missing from the publish ($dfu)" >&2; exit 1; }
chmod 755 "$dfu/dfu-util"
xattr -cr "$app"
codesign --force --sign - "$dfu/libusb-1.0.0.dylib"
codesign --force --sign - "$dfu/dfu-util"
codesign --force --deep --sign - "$app"
codesign --verify --deep "$app"

# the in-place update package (HELP > updates): the signed bundle, zipped with ditto so permissions survive
upd="bin/MissionPlanner-$version-$rid-update.zip"
rm -f "$upd"
ditto -c -k --keepParent "$app" "$upd"
echo "Built $root/$upd ($(du -h "$upd" | cut -f1))"

# the disk image: the app and a link to drag it onto
img="$stage/image"
mkdir -p "$img"
mv "$app" "$img/"
ln -s /Applications "$img/Applications"
out="bin/MissionPlanner-$version-$rid.dmg"
rm -f "$out"
hdiutil create -volname "Mission Planner $version" -srcfolder "$img" -fs HFS+ -format UDZO -ov "$out" >/dev/null
echo "Built $root/$out ($(du -h "$out" | cut -f1))"

# the same file without the version, for the fixed .../releases/latest/download/ link: attach both to the release
mkdir -p bin/latest
cp "$out" "bin/latest/MissionPlanner-$rid.dmg"
echo "Copy for the latest link: $root/bin/latest/MissionPlanner-$rid.dmg"
