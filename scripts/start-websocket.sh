#!/usr/bin/env bash

# This is a dumb hack to keep the service alive. It will auto-respawn the daemon if it crashes.
# And it will crash, because this is still the very early stages of development.
# Ideally, this would have a systemd unit file or something similar inside of the container, but
# this bandaid is good enough for right now.

LOGDIR="/var/log/openversus"
CRASHDIR="$LOGDIR/crashes"
if [ ! -d "$LOGDIR" ]; then
    mkdir -p "$LOGDIR"
fi
if [ ! -d "$CRASHDIR" ]; then
    mkdir -p "$CRASHDIR"
fi

function WebSocketKeepAlive
{
    LOGFILENAME="websocket-$(date --iso-8601='ns' | cut -d+ -f1)"
    node "build/src/websocketStart.js" 2>&1 | tee -a "$LOGDIR/$LOGFILENAME.log"
}

until WebSocketKeepAlive; do
    echo "[$(date)] Server 'websocket' crashed with exit code $? at time: $(date --iso-8601='ns' | cut -d+ -f1). Respawning..." 2>&1 | tee -a "$CRASHDIR/websocket-crashes.log"
    sleep 1
done
