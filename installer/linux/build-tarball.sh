#!/bin/bash
# Builds the Linux package of Mission Planner 2 (src/MissionPlanner.App): a self-contained
# publish (no .NET install needed on the target) packed as bin/MissionPlanner-<version>-<rid>.tar.gz
# with a top-level MissionPlanner-<version>/ folder. Run on Linux with the .NET 10 SDK:
#   installer/linux/build-tarball.sh [linux-x64|linux-arm64]
# The version is MissionPlannerVersion in src/Directory.Build.props.
set -euo pipefail
rid="${1:-linux-x64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"

version="$(sed -n 's:.*<MissionPlannerVersion>\(.*\)</MissionPlannerVersion>.*:\1:p' src/Directory.Build.props | head -1)"
[ -n "$version" ] || { echo "MissionPlannerVersion not found in src/Directory.Build.props" >&2; exit 1; }
echo "Mission Planner $version, $rid"

name="MissionPlanner-$version"
stage="bin/tarball-$rid"
rm -rf "$stage"
dotnet publish src/MissionPlanner.App -c Release -r "$rid" --self-contained true -o "$stage/$name"
find "$stage/$name" -name '*.pdb' -delete
# plain permissions whatever the checkout had (a tree copied from a Windows drive is all 0777)
find "$stage/$name" -type d -exec chmod 755 {} +
find "$stage/$name" -type f -exec chmod 644 {} +
chmod 755 "$stage/$name/MissionPlanner.App" "$stage/$name/createdump"

out="bin/$name-$rid.tar.gz"
tar -C "$stage" --owner=0 --group=0 -czf "$out" "$name"
echo "Built $root/$out ($(du -h "$out" | cut -f1))"
