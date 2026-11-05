#!/usr/bin/env bash

function die
{
  local message=$1
  [ -z "$message" ] && message="Died"
  echo "${BASH_SOURCE[1]}: line ${BASH_LINENO[0]}: ${FUNCNAME[1]}: $message." >&2
  exit 1
}

# This is an UGLY hack to run multiple processes in a single container and keep them alive. We should
# ideally split these into separate containers, but for now this is the easiest way
# to get everything running without setting up a proper orchestration system.

cd /app || die "Failed to change directory to /app"

/startup/start-index.sh &
/startup/start-loader.sh &
/startup/start-websocket.sh &
/startup/start-worker.sh &
/app/rollback-server 2>&1 >>/var/log/openversus/rollback-server.log
