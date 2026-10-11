#!/usr/bin/env bash
set -euo pipefail

# Commit text remains data, never interpolated into shell source.
tag="build-${GITHUB_SHA}"
title=$(git log -1 --format=%s "$GITHUB_SHA")
notes=$(mktemp)
trap 'rm -f "$notes"' EXIT
git log -1 --format=%B "$GITHUB_SHA" > "$notes"
asset=vrc-network-monitor-win-x64.zip
test -s "$asset"

# Keep this check in the publishing step, immediately before creating any tag
# or release. API failures fail closed rather than publishing an unchecked SHA.
head=$(gh api "repos/${GITHUB_REPOSITORY}/git/ref/${GITHUB_REF#refs/}" --jq '.object.sha')
if [[ "$head" != "$GITHUB_SHA" ]]; then
    echo "Skipping release: ${GITHUB_SHA} was superseded by ${head}."
    exit 0
fi

gh release create "$tag" "$asset" \
    --repo "$GITHUB_REPOSITORY" \
    --target "$GITHUB_SHA" \
    --title "$title" \
    --notes-file "$notes" \
    --latest
