#!/bin/sh

atd

"${WEBHOOK_BIN}" "-template" "-hooks" "${WEBHOOK_HOOKSFILE}" "-hotreload" "-logfile" "${WEBHOOK_LOGFILE}" "-port" "${WEBHOOK_PORT}" "-setgid" "${WEBHOOK_GID}" "-setuid" "${WEBHOOK_UID}" "-x-request-id" &

WEBHOOKCHILDPID=$!

cleanup() {
    kill -s TERM $WEBHOOKCHILDPID
}
echo "Webhook PID in Docker container is $WEBHOOKCHILDPID"
echo "CTRL+C to exit"

trap 'cleanup; exit 130' INT
trap 'cleanup; exit 143' TERM

wait $WEBHOOKCHILDPID
