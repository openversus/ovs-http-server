#!/usr/bin/env bash
# Runs the test suites against stores of their own, never a live one: Redis in the container ovstest-redis
# (127.0.0.1:$OVS_TEST_REDIS_PORT, default 16380; created on first use), and Mongo databases named for the tests, each
# dropped afterwards (OVS_TEST_MONGO, default the local Mongo). A Redis shared with running services would hear the
# tests: channels ignore the database number, so whatever the code under test publishes reaches those services.
# Arguments go to dotnet test (e.g. --filter).
set -euo pipefail
cd "$(dirname "$0")"
port=${OVS_TEST_REDIS_PORT:-16380}
if ! docker ps --format '{{.Names}}' | grep -qx ovstest-redis; then
  docker start ovstest-redis >/dev/null 2>&1 || docker run -d --name ovstest-redis -p "127.0.0.1:$port:6379" redis:8-alpine >/dev/null
fi
export OVS_TEST_REDIS=127.0.0.1:$port OVS_TEST_REDIS_USER= OVS_TEST_REDIS_PW=
export OVS_TEST_MONGO=${OVS_TEST_MONGO:-mongodb://127.0.0.1:27017/ovs_ctl_tests}
export OVS_TEST_HYDRA_CORPUS=${OVS_TEST_HYDRA_CORPUS:-$PWD/local/hydra-corpus}
# The services' own variables would leak into the test hosts.
exec env -u REDIS -u REDIS_PORT -u REDIS_USERNAME -u REDIS_PW -u MONGODB_URI -u JWT_SECRET dotnet test OpenVersus.Server.slnx "$@"
