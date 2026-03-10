#!/usr/bin/env bash

function die
{
  local message=$1
  [ -z "$message" ] && message="Died"
  echo "${BASH_SOURCE[1]}: line ${BASH_LINENO[0]}: ${FUNCNAME[1]}: $message." >&2
  exit 1
}

UDPPORT=$1
ENTRYPOINT=$2
OVS_SERVER=$3
SERVICENAME=$4
IMAGE=$5
COMPOSEDIR="/etc/webhook/compose-files"

echo "args are: "
echo "1: $1"
echo "2: $2"
echo "3: $3"
echo "4: $4"
echo "5: $5"


if [ -z "$UDPPORT" ]; then
  UDPPORT=41234
fi

if [ -z "$ENTRYPOINT" ]; then
  ENTRYPOINT=$(echo \"dotnet\", \"OVS.Rollback.Server.dll\", \"${UDPPORT}\")
fi

if [ -z "$OVS_SERVER" ]; then
  OVS_SERVER="http://testing.openversus.org:8000"
fi

if [ -z "$SERVICENAME" ]; then
  SERVICENAME="ovs-rollback-server-csharp"
fi

if [ -z "$IMAGE" ]; then
  IMAGE="${SERVICENAME}:latest"
fi

SERVICENAME="${SERVICENAME}-${UDPPORT}"
COMPOSETEMPLATE=$(cat <<EOF
services:
  ${SERVICENAME}:
    image: ${IMAGE}
    deploy:
      resources:
        limits:
          cpus: '2'
    entrypoint: [${ENTRYPOINT}]
    environment:
      OVS_SERVER: ${OVS_SERVER}
    ports:
      - "${UDPPORT}:${UDPPORT}/udp"
    restart: no

EOF
)

echo "Compose template will be: "
echo "$COMPOSETEMPLATE"

mkdir -p "${COMPOSEDIR}/${SERVICENAME}" || die "Failed to create directory ${COMPOSEDIR}/${SERVICENAME}"
echo "${COMPOSETEMPLATE}" >"${COMPOSEDIR}/${SERVICENAME}/docker-compose.yml" || die "Failed to write docker-compose.yml"
cd "${COMPOSEDIR}/${SERVICENAME}" || die "Failed to change directory to ${COMPOSEDIR}/${SERVICENAME}"
sudo /usr/local/bin/docker compose up -d || die "Failed to start docker compose"
