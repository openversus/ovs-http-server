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
    restart: unless-stopped

EOF
)

STACKDIRECTORY="${COMPOSEDIR}/${SERVICENAME}"
ISBUSYBOX=$(diff /bin/date /bin/busybox &>/dev/null; echo $?)
if [ "$ISBUSYBOX" -eq 0 ]; then
  LOGSTIMESTAMP=$(date -Ins | cut -d+ -f1)
else
  LOGSTIMESTAMP=$(date --iso-8601="ns" | cut -d+ -f1)
fi
ROLLBACKLOGSDIR="/var/log/webhook/rollback_logs"

SELFDESTRUCTTEMPLATE=$(cat <<EOF
#!/usr/bin/env bash

function die
{
  local message=\$1
  [ -z "\$message" ] && message="Died"
  echo "\${BASH_SOURCE[1]}: line \${BASH_LINENO[0]}: \${FUNCNAME[1]}: \$message." >&2
  exit 1
}

if [ ! -d "$ROLLBACKLOGSDIR" ]; then
  mkdir -p "$ROLLBACKLOGSDIR" || echo "Could not create rollback server logs directory"
fi

if [ -z "$STACKDIRECTORY" ]; then
  die "Compose stack directory \"${STACKDIRECTORY}\" does not exist."
  exit 1;
fi

cd "$STACKDIRECTORY" || die "Could not cd to ${STACKDIRECTORY}"

CONTAINERLOGS=\$(sudo docker compose logs)
CONTAINERHOSTNAME=\$(sudo docker compose ps | tail -1 | awk '{print \$1}' | sudo xargs docker inspect | grep "Hostname\"" | cut -d\" -f 4)
CONTAINERLOGFILE="${ROLLBACKLOGSDIR}/\${CONTAINERHOSTNAME}-${LOGSTIMESTAMP}.log"

echo "\${CONTAINERLOGS}" >"\${CONTAINERLOGFILE}" || echo "Could not save container logs"
sudo docker compose down || die "Could not stop rollback server stack in directory ${STACKDIRECTORY}"
echo "Destroyed rollback host \"\${CONTAINERHOSTNAME}\" at date: ${LOGSTIMESTAMP}"
#rm -rf "${STACKDIRECTORY}" || die "Could not remove compose stack directory \"${STACKDIRECTORY}\" for rollback host \${CONTAINERHOSTNAME}"
EOF
)

echo "Compose template will be: "
echo "$COMPOSETEMPLATE"

mkdir -p "${COMPOSEDIR}/${SERVICENAME}" || die "Failed to create directory ${COMPOSEDIR}/${SERVICENAME}"
echo "${COMPOSETEMPLATE}" >"${COMPOSEDIR}/${SERVICENAME}/docker-compose.yml" || die "Failed to write docker-compose.yml"
cd "${COMPOSEDIR}/${SERVICENAME}" || die "Failed to change directory to ${COMPOSEDIR}/${SERVICENAME}"
sudo /usr/local/bin/docker compose up -d
LAUNCHSUCCESS=$?

if [ $LAUNCHSUCCESS -ne 0 ]; then
  die "Failed to launch docker compose stack for ${SERVICENAME}"
fi

echo "Launched docker compose stack for ${SERVICENAME} successfully"
SELFDESTRUCTPATH="${COMPOSEDIR}/${SERVICENAME}/rollback-self-destruct-${UDPPORT}.sh"
echo "${SELFDESTRUCTTEMPLATE}" >"${SELFDESTRUCTPATH}" && chmod a+x "${SELFDESTRUCTPATH}"
SELFDESTRUCTSUCCESS=$?

if [ $SELFDESTRUCTSUCCESS -ne 0 ]; then
    echo "Warning: Failed to write self-destruct script for ${SERVICENAME}"
else
    ATDRUNNING=$(ps -ef | grep atd | grep -v grep)
    if [ -z "$ATDRUNNING" ]; then
        echo "Warning: atd does not appear to be running, attempting to start it..."
        /usr/sbin/atd || echo "Warning: Failed to start atd, self-destruct script for ${SERVICENAME} may not run"
        ATDRUNNING=$(ps -ef | grep atd | grep -v grep)
    fi

    if [ "$ATDRUNNING" ]; then
        at now + 9 minutes <<< $(cat ${SELFDESTRUCTPATH})
    fi
fi
