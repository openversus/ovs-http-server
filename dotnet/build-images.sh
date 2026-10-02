#!/usr/bin/env bash
# Builds the C# services' images, one per service: ovs-http, ovs-access, ovs-social, ovs-lobbies, ovs-web, ovs-matchmaking, ovs-matchflow, tagged $1 (default: latest).
# The commit goes into each image's version (ovsctl status); "-dirty" when the working tree has uncommitted changes.
set -euo pipefail
cd "$(dirname "$0")"
tag=${1:-latest}
revision=$(git rev-parse HEAD 2>/dev/null || echo unknown)
git diff --quiet HEAD 2>/dev/null || revision="$revision-dirty"
for service in http access social lobbies web matchmaking matchflow; do
  docker build --target "$service" --build-arg SOURCE_REVISION="$revision" -t "ovs-$service:$tag" .
done
