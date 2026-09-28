#!/usr/bin/env bash
# Builds and runs eng/consumer-smoke against the single .nupkg in $1, outside the repo's MSBuild imports.
set -euo pipefail

artifacts=$(cd "$1" && pwd)
repo=$(cd "$(dirname "$0")/../.." && pwd)
shopt -s nullglob
packages=("$artifacts"/Aspire.Hosting.DocumentDB.*.nupkg)
if [ ${#packages[@]} -ne 1 ]; then
  echo "Expected exactly one Aspire.Hosting.DocumentDB nupkg in $artifacts, found ${#packages[@]}." >&2
  exit 1
fi
version=$(basename "${packages[0]}" .nupkg)
version=${version#Aspire.Hosting.DocumentDB.}

work=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/consumer-smoke.XXXXXX")
cp "$repo"/eng/consumer-smoke/{ConsumerSmoke.csproj,Program.cs,nuget.config} "$repo/global.json" "$work"/
mkdir "$work/packages"
cp "${packages[0]}" "$work/packages/"

# A private package cache, so a same-version copy already restored from nuget.org can't stand in for the local build.
export NUGET_PACKAGES="$work/.nuget"
dotnet run --project "$work/ConsumerSmoke.csproj" -p:DocumentDBPackageVersion="$version"
