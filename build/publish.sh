#!/usr/bin/env bash
# Builds self-contained DeskMaster packages (desktop app + CLI).
# Usage: ./build/publish.sh [rid ...]      e.g. ./build/publish.sh linux-x64 osx-arm64
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts"
rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
  rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

dotnet test "$root/tests/DeskMaster.Core.Tests" -c Release --nologo

for rid in "${rids[@]}"; do
  dir="$out/DeskMaster-$rid"
  rm -rf "$dir"
  for project in src/DeskMaster.App src/DeskMaster.Cli; do
    dotnet publish "$root/$project" -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
      -o "$dir" --nologo
  done
  rm -f "$dir"/*.pdb
  cp "$root/LICENSE" "$root/README.md" "$dir/"
  (cd "$out" && tar -czf "DeskMaster-$rid.tar.gz" "DeskMaster-$rid")
  echo "Packaged $out/DeskMaster-$rid.tar.gz"
done
