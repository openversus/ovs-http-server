#!/usr/bin/env bash

OVS_ROOT_DIR="/opt/docker/openversus"
DBS_DIR="dbs"
INDEX_DIR="index"
WS_DIR="ws"
MM_DIR="mm"

cd "${OVS_ROOT_DIR}/${MM_DIR}" || die "Could not cd to ${MM_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${WS_DIR}" || die "Could not cd to ${WS_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${INDEX_DIR}" || die "Could not cd to ${INDEX_DIR} directory" \
  && docker compose down \
  && cd "${OVS_ROOT_DIR}/${DBS_DIR}" || die "Could not cd to ${DBS_DIR} directory" \
  && docker compose down
