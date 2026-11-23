#!/bin/bash
# Builds the iPhone app of Mission Planner 3 (src/MissionPlanner.Mobile.iOS) for the App Store and,
# with an App Store Connect API key, signs it in the cloud and uploads it to App Store Connect
# (TestFlight). Needs a Mac with Xcode and the .NET iOS workload (dotnet workload install ios).
#
#   installer/ios/build-ipa.sh                       # build bin/ios/MissionPlanner.xcarchive only
#   ASC_KEY=~/private_keys/AuthKey_XXXX.p8 ASC_KEY_ID=XXXX ASC_ISSUER=uuid TEAM_ID=XXXXXXXXXX \
#   BUILD_NUMBER=2 installer/ios/build-ipa.sh upload  # also sign and upload
#
# The app is built unsigned; xcodebuild -exportArchive signs it with an Apple Distribution
# certificate and App Store profile it manages itself (cloud signing), so the Mac needs no
# distribution certificate in its keychain.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

version="$(sed -n 's:.*<MissionPlannerVersion>\(.*\)</MissionPlannerVersion>.*:\1:p' src/Directory.Build.props | head -1)"
short="$(echo "$version" | grep -oE '^[0-9]+(\.[0-9]+){0,2}')"
build="${BUILD_NUMBER:-1}"
echo "Mission Planner $version (store version $short, build $build)"

out="$root/bin/ios"
rm -rf "$out" && mkdir -p "$out"
dotnet build src/MissionPlanner.Mobile.iOS -c Release -r ios-arm64 \
    -p:IosBuildNumber="$build" -p:EnableCodeSigning=false -p:ValidateXcodeVersion=false \
    -p:BuildIpa=false -o "$out/build"

app="$(ls -d "$out"/build/*.app 2>/dev/null | head -1)"
[ -n "$app" ] || app="$(ls -d src/MissionPlanner.Mobile.iOS/bin/Release/net10.0-ios/ios-arm64/*.app | head -1)"
bundle_id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Info.plist")"

# an Xcode archive around the app, which is what the export step signs and uploads
archive="$out/MissionPlanner.xcarchive"
mkdir -p "$archive/Products/Applications"
cp -R "$app" "$archive/Products/Applications/"
cat > "$archive/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>ApplicationProperties</key>
  <dict>
    <key>ApplicationPath</key><string>Applications/$(basename "$app")</string>
    <key>CFBundleIdentifier</key><string>$bundle_id</string>
    <key>CFBundleShortVersionString</key><string>$short</string>
    <key>CFBundleVersion</key><string>$build</string>
    <key>Architectures</key><array><string>arm64</string></array>
  </dict>
  <key>ArchiveVersion</key><integer>2</integer>
  <key>CreationDate</key><date>$(date -u +%Y-%m-%dT%H:%M:%SZ)</date>
  <key>Name</key><string>Mission Planner</string>
  <key>SchemeName</key><string>MissionPlanner</string>
</dict>
</plist>
EOF
echo "Archive: $archive"

[ "${1:-}" = "upload" ] || exit 0

: "${ASC_KEY:?path of the App Store Connect API key (.p8)}" "${ASC_KEY_ID:?key id}" "${ASC_ISSUER:?issuer id}" "${TEAM_ID:?team id}"
cat > "$out/export.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>method</key><string>app-store-connect</string>
  <key>destination</key><string>upload</string>
  <key>teamID</key><string>$TEAM_ID</string>
  <key>signingStyle</key><string>automatic</string>
  <key>uploadSymbols</key><false/>
  <key>manageAppVersionAndBuildNumber</key><false/>
</dict>
</plist>
EOF
xcodebuild -exportArchive -archivePath "$archive" -exportOptionsPlist "$out/export.plist" -exportPath "$out/export" \
    -allowProvisioningUpdates -authenticationKeyPath "$ASC_KEY" -authenticationKeyID "$ASC_KEY_ID" \
    -authenticationKeyIssuerID "$ASC_ISSUER"
echo "Uploaded build $build to App Store Connect"
