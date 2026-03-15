#!/usr/bin/env bash

function die
{
  local message=$1
  [ -z "$message" ] && message="Died"
  echo "${BASH_SOURCE[1]}: line ${BASH_LINENO[0]}: ${FUNCNAME[1]}: $message." >&2
  exit 1
}

CURRENTDIR=$(pwd)
INPUTDIRECTORY=$1
SERVICENAME=$2
UDPPORT=$3
COMPOSEDIR="/etc/webhook/compose-files"
ROLLBACKLOGSDIR="/var/log/webhook/rollback_logs"
ISBUSYBOX=$(diff /bin/date /bin/busybox &>/dev/null; echo $?)

if [ "$ISBUSYBOX" -eq 0 ]; then
  LOGSTIMESTAMP=$(date -Ins | cut -d+ -f1)
else
  LOGSTIMESTAMP=$(date --iso-8601="ns" | cut -d+ -f1)
fi

echo "args are: "
echo "1: $1"
echo "2: $2"
echo "3: $3"

if [ ! -d "$ROLLBACKLOGSDIR" ]; then
  mkdir -p "$ROLLBACKLOGSDIR" || echo "Could not create rollback server logs directory"
fi

if [ -z "$INPUTDIRECTORY" ]; then
  if [ -z "$SERVICENAME" ] || [ -z "$UDPPORT" ]; then
    die "Invalid argumens"
    exit 1
  fi
  STACKDIRECTORY="${COMPOSEDIR}/${SERVICENAME}-${$UDPPORT}"

else
  STACKDIRECTORY="${COMPOSEDIR}/${INPUTDIRECTORY}"
fi

if [ -z "$STACKDIRECTORY" ]; then
  die "Compose stack directory \"${STACKDIRECTORY}\" does not exist."
  exit 1;
fi

cd "$STACKDIRECTORY" || die "Could not cd to ${STACKDIRECTORY}"

CONTAINERLOGS=$(sudo docker compose logs)
CONTAINERHOSTNAME=$(sudo docker compose ps | tail -1 | awk '{print $1}' | sudo xargs docker inspect | grep "Hostname\"" | cut -d\" -f 4)
CONTAINERLOGFILE="${ROLLBACKLOGSDIR}/${CONTAINERHOSTNAME}-${LOGSTIMESTAMP}.log"

echo "${CONTAINERLOGS}" >"${CONTAINERLOGFILE}" || echo "Could not save container logs"
sudo docker compose down || die "Could not stop rollback server stack in directory ${STACKDIRECTORY}"
rm -rf "${STACKDIRECTORY}" || die "Could not remove compose stack directory \"${STACKDIRECTORY}\" for rollback host ${CONTAINERHOSTNAME}"
echo "Destroyed rollback host \"${CONTAINERHOSTNAME}\" at date: ${LOGSTIMESTAMP}"

