#!/bin/bash
# Builds the Android app of Mission Planner 3 (src/MissionPlanner.Mobile.Android) as
# bin/MissionPlanner-<version>-android-arm64.apk, signed with the build machine's debug key
# (fine for installing by hand; a store release needs its own key).
# Needs the .NET 10 SDK with the android workload (dotnet workload install android), a JDK 17+
# and an Android SDK with platform 36:
#   JAVA_HOME=... ANDROID_HOME=... installer/android/build-apk.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"

version="$(sed -n 's:.*<MissionPlannerVersion>\(.*\)</MissionPlannerVersion>.*:\1:p' src/Directory.Build.props | head -1)"
[ -n "$version" ] || { echo "MissionPlannerVersion not found in src/Directory.Build.props" >&2; exit 1; }
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
jdk="${JAVA_HOME:?set JAVA_HOME to a JDK 17 or newer}"
echo "Mission Planner $version, android-arm64 (SDK $sdk, JDK $jdk)"

out="bin/android"
rm -rf "$out"
dotnet publish src/MissionPlanner.Mobile.Android -c Release -f net10.0-android -r android-arm64 \
    -p:AndroidSdkDirectory="$sdk" -p:JavaSdkDirectory="$jdk" -o "$out"

apk="$(ls "$out"/*-Signed.apk | head -1)"
dest="bin/MissionPlanner-$version-android-arm64.apk"
cp "$apk" "$dest"
echo "Built $root/$dest ($(du -h "$dest" | cut -f1))"

# the same file without the version, for the fixed .../releases/latest/download/ link
mkdir -p bin/latest
cp "$dest" "bin/latest/MissionPlanner-android-arm64.apk"
echo "Copy for the latest link: $root/bin/latest/MissionPlanner-android-arm64.apk"
