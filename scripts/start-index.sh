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

function IndexKeepAlive
{
    LOGFILENAME="index-$(date --iso-8601='ns' | cut -d+ -f1)"
    node "build/src/index.js" 2>&1 | tee -a "$LOGDIR/$LOGFILENAME.log"
}

until IndexKeepAlive; do
    echo "[$(date)] Server 'index' crashed with exit code $? at time: $(date --iso-8601='ns' | cut -d+ -f1). Respawning..." 2>&1 | tee -a "$CRASHDIR/index-crashes.log"
    sleep 1
done
