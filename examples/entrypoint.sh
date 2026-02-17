#!/usr/bin/env bash

cd /app || die "Failed to change to /app directory"

/startup/start-index.sh &
/startup/start-loader.sh &
/startup/start-websocket.sh &
/startup/start-worker.sh &
/app/rollback-server 2>&1 | tee -a "/var/log/openversus/rollback-server.log"
