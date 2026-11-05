#!/usr/bin/env bash

function die
{
  local message=$1
  [ -z "$message" ] && message="Died"
  echo "${BASH_SOURCE[1]}: line ${BASH_LINENO[0]}: ${FUNCNAME[1]}: $message." >&2
  exit 1
}

OVS_ROOT_DIR="/opt/docker/openversus"
DBS_DIR="dbs"
WEBHOOK_DIR="webhook"
INDEX_DIR="index"
WS_DIR="ws"
MM_DIR="mm"

cd "${OVS_ROOT_DIR}/${MM_DIR}" || die "Could not cd to ${MM_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${WS_DIR}" || die "Could not cd to ${WS_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${INDEX_DIR}" || die "Could not cd to ${INDEX_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${WEBHOOK_DIR}" || die "Could not cd to ${WEBHOOK_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${DBS_DIR}" || die "Could not cd to ${DBS_DIR} directory" \
  && docker compose down
