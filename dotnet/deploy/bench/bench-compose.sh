#!/usr/bin/env bash
# The bench on Compose: the services project with the bench's variables and override, beside the live bench.sh bench.
#
#   dotnet/deploy/bench/bench-compose.sh dbs up -d          the stores (project openversus-dbs, its own volumes)
#   dotnet/deploy/bench/bench-compose.sh up -d [--build]    the services (project openversus) with the bench override
#   dotnet/deploy/bench/bench-compose.sh ps | logs -f web | down | build ...   anything else is passed to compose
#
# Variables come from ~/git/ovs-local-dev/compose (env files, redis.conf, the control sockets under run/); the data
# files from ~/git/ovs-local-dev/data and the node keys from the rollback repository's local/pki/bench, the same pair bench.sh uses.
# Ports: the router on 127.0.0.1:18300, the edge on 127.0.0.1:13301 (the live bench keeps 18000 and 13001).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
dev=${OVS_LOCAL_DEV:-$HOME/git/ovs-local-dev}
export OVS_UID=$(id -u) OVS_GID=$(id -g) OVS_LOG_DIR="$dev/compose/logs"
export OVS_ENV_DIR="$dev/compose" OVS_DATA_DIR="$dev/data" OVS_KEYS_DIR="${OVS_ROLLBACK_REPO:-$HOME/git/ovs-rollback-server}/local/pki/bench" OVS_RUN_DIR="$dev/compose/run"
export OVS_TAG=${OVS_TAG:-bench} OVS_SOURCE_REVISION=${OVS_SOURCE_REVISION:-$(git -C "$here" rev-parse --short HEAD)}
export OVS_GAME_PORT=${OVS_GAME_PORT:-18300} OVS_EDGE_PORT=${OVS_EDGE_PORT:-13301}
mkdir -p "$OVS_RUN_DIR" "$OVS_LOG_DIR"
if [ "${1:-}" = dbs ]; then
  shift
  exec docker compose -f "$here/../dbs/compose.yaml" "$@"
fi
exec docker compose -f "$here/../services/compose.yaml" -f "$here/compose.override.yaml" "$@"
