#!/usr/bin/env bash

# This is a dumb hack to keep the service alive. It will auto-respawn the daemon if it crashes.
# And it will crash, because this is still the very early stages of development.
# Ideally, this would have a systemd unit file or something similar inside of the container, but
# this bandaid is good enough for right now.
#
# The loader most likely doesn't need to be kept running and should just be a one-shot on startup, but
# I haven't tried it yet. It most likely is that way, though.

LOGDIR="/var/log/openversus"
CRASHDIR="$LOGDIR/crashes"
if [ ! -d "$LOGDIR" ]; then
    mkdir -p "$LOGDIR"
fi
if [ ! -d "$CRASHDIR" ]; then
    mkdir -p "$CRASHDIR"
fi

function LoaderKeepAlive
{
    LOGFILENAME="loader-$(date --iso-8601='ns' | cut -d+ -f1)"
    node "build/src/loader.js" 2>&1 | tee -a "$LOGDIR/$LOGFILENAME.log"
}

until LoaderKeepAlive; do
    echo "[$(date)] Server 'loader' crashed with exit code $? at time: $(date --iso-8601='ns' | cut -d+ -f1). Respawning..." 2>&1 | tee -a "$CRASHDIR/loader-crashes.log"
    sleep 1
done

