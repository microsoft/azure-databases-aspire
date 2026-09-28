#!/usr/bin/env bash
# Fails if any locally pulled documentdb-local tag that the digest lock knows is not the locked image.
# AppHost tests pull by tag (WithOpenTelemetryMetrics rejects digest pins), so this checks what they got.
set -euo pipefail

repo=ghcr.io/documentdb/documentdb/documentdb-local
lock=$(dirname "$0")/../documentdb-image-digests.json
# Listed outside the process substitution, whose exit status bash ignores, so a docker failure fails the check.
tags=$(docker image ls "$repo" --format '{{.Tag}}')
status=0
while read -r tag; do
  locked=$(jq -r --arg t "$tag" '.[$t].index // empty' "$lock")
  [ -n "$locked" ] || continue
  if ! docker image inspect "$repo:$tag" --format '{{json .RepoDigests}}' | jq -e --arg d "$repo@$locked" 'any(.[]; . == $d)' > /dev/null; then
    echo "::error::$repo:$tag is not the locked $locked"
    status=1
  fi
done < <(grep -v '<none>' <<< "$tags" || true)
exit "$status"
