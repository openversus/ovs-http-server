#!/usr/bin/env bash

function die
{
  local message=$1
  [ -z "$message" ] && message="Died"
  echo "${BASH_SOURCE[1]}: line ${BASH_LINENO[0]}: ${FUNCNAME[1]}: $message." >&2
  exit 1
}

cd /app || die "Failed to change to /app directory"

/startup/start-index.sh &
/startup/start-loader.sh &
/app/rollback-server 2>&1 | tee -a "/var/log/openversus/rollback-server.log"
