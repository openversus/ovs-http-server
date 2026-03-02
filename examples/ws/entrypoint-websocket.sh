#!/usr/bin/env bash

cd /app || die "Failed to change to /app directory"

/startup/start-websocket.sh
