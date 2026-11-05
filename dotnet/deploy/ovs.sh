#!/usr/bin/env bash
# The deployment on its host: the stores (dbs/) and the services (services/) as two Compose projects, set up by .env
# beside this script (from env/deploy.env.example) and the files in OVS_ENV_DIR (from env/*.example).
#
#   ./ovs.sh setup                      directories, env templates (never overwrites one), the bundle's images, checks
#   ./ovs.sh check                      the checks alone
#   ./ovs.sh up [all|dbs|services]      start (default all; `services` alone when the stores run elsewhere)
#   ./ovs.sh down [services|dbs|all] [--now]
#                                       stop (default services); volumes are never removed. The edge waits for its
#                                       games to leave (hours); --now drops them
#   ./ovs.sh status                     both projects' containers
#   ./ovs.sh logs [service...]          follow the services' logs
#   ./ovs.sh restore ARCHIVE [--from DB] [--yes]
#                                       a mongodump archive (.archive.zst; its database DB, default test: the TS
#                                       server's) into the dbs project's Mongo as MONGODB_URI's database, its Redis
#                                       flushed, the running services restarted
#   ./ovs.sh compose dbs|services ARGS  any other docker compose command with this deployment's settings,
#                                       e.g. ./ovs.sh compose services exec matchflow ovsctl settings list
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
env_file=${OVS_DEPLOY_ENV:-$here/.env}

say() { printf '== %s\n' "$*"; }
fail() { printf 'ovs.sh: %s\n' "$*" >&2; exit 1; }
usage() { awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"; }

command=${1:-}
[ -n "$command" ] || { usage; exit 2; }
shift
case "$command" in -h|--help|help) usage; exit 0 ;; esac

[ -f "$env_file" ] || fail "no $env_file: cp $here/env/deploy.env.example $env_file and edit it"
# images.env (from build.sh) first, so that .env can override a tag.
set -a
[ -f "$here/images.env" ] && . "$here/images.env"
. "$env_file"
set +a
project=${OVS_PROJECT:-openversus}
export OVS_NETWORK=${OVS_NETWORK:-ovs}

dc_dbs() { docker compose -p "$project-dbs" -f "$here/dbs/compose.yaml" "$@"; }
dc_svc() { docker compose -p "$project" -f "$here/services/compose.yaml" "$@"; }

# A KEY=value line's value from an env file ("" when the file or the key is missing).
value() { [ -f "$1" ] && sed -n "s/^$2=//p" "$1" | tail -1 || true; }

dirs=(OVS_ENV_DIR OVS_DATA_DIR OVS_KEYS_DIR OVS_RUN_DIR OVS_LOG_DIR)
env_files=(services.env relay.env mongo.env redis.conf)

do_setup() {
    for name in "${dirs[@]}"; do
        [ -n "${!name:-}" ] || fail "$name is not set in $env_file"
    done
    say "directories"
    for name in "${dirs[@]}"; do
        dir=${!name}
        if [ ! -d "$dir" ]; then
            mkdir -p "$dir"
            [ "$name" = OVS_ENV_DIR ] && chmod 700 "$dir"
            echo "   created $dir"
        fi
        # The services run as OVS_UID:OVS_GID and write the sockets and logs, read the data and keys; the env files
        # are read by docker compose, so OVS_ENV_DIR stays with whoever runs this.
        if [ "$name" != OVS_ENV_DIR ] && [ "$(id -u)" = 0 ]; then
            chown "${OVS_UID:-1654}:${OVS_GID:-1654}" "$dir"
        fi
    done

    say "env files in $OVS_ENV_DIR"
    for f in "${env_files[@]}"; do
        if [ -f "$OVS_ENV_DIR/$f" ]; then
            echo "   $f: present (left as it is)"
        else
            cp "$here/env/${f%.env}.env.example" "$OVS_ENV_DIR/$f" 2>/dev/null || cp "$here/env/$f.example" "$OVS_ENV_DIR/$f"
            # docker compose reads the env files; redis.conf is mounted into the container, whose redis user must read
            # it. OVS_ENV_DIR itself (700) keeps the host's other users out.
            if [ "$f" = redis.conf ]; then chmod 644 "$OVS_ENV_DIR/$f"; else chmod 600 "$OVS_ENV_DIR/$f"; fi
            echo "   $f: copied from the template: EDIT IT"
        fi
    done

    shopt -s nullglob
    bundles=("$here"/images-*.tar.zst)
    shopt -u nullglob
    if [ ${#bundles[@]} -eq 0 ]; then
        echo "   (no images-*.tar.zst here: the images must already be on this host)"
    fi
    for b in "${bundles[@]}"; do
        say "loading $(basename "$b")"
        zstd -dc "$b" | docker load | sed 's/^/   /'
    done
    do_check || true
}

# Prints what would stop a start (problems) and what only looks off (warnings); fails on problems.
do_check() {
    local problems=0 svc="$OVS_ENV_DIR/services.env" relay_env="$OVS_ENV_DIR/relay.env"
    problem() { printf '   PROBLEM: %s\n' "$*"; problems=$((problems + 1)); }
    warn() { printf '   warning: %s\n' "$*"; }
    say "checks (project $project, network $OVS_NETWORK)"
    for name in "${dirs[@]}"; do
        [ -n "${!name:-}" ] && [ -d "${!name}" ] || problem "$name (${!name:-unset}) is not a directory: ./ovs.sh setup"
    done
    for f in "${env_files[@]}"; do
        if [ ! -f "$OVS_ENV_DIR/$f" ]; then
            problem "$OVS_ENV_DIR/$f is missing: ./ovs.sh setup copies the template"
        elif grep -q 'change-me' "$OVS_ENV_DIR/$f"; then
            problem "$f still has template values (change-me): $(grep 'change-me' "$OVS_ENV_DIR/$f" | sed -E 's/^(user [^ ]+).*/\1/; s/=.*//' | tr '\n' ' ')"
        fi
    done
    if [ -f "$OVS_ENV_DIR/redis.conf" ] && [ $(( 0$(stat -c %a "$OVS_ENV_DIR/redis.conf") & 4 )) -eq 0 ]; then
        problem "redis.conf is not readable by others: the Redis container's own user must read it (chmod 644; OVS_ENV_DIR keeps it private)"
    fi
    case "${OVS_RELAY_ADDRESS:-}" in
        ""|127.0.0.1) problem "OVS_RELAY_ADDRESS is ${OVS_RELAY_ADDRESS:-unset}: P2P nodes elsewhere get no relay fallback" ;;
    esac
    # Files the services read under /data and /keys, by the paths services.env gives them.
    if [ -f "$svc" ]; then
        while read -r path; do
            [ -f "$OVS_KEYS_DIR/${path#/keys/}" ] || problem "$path (services.env) is not in OVS_KEYS_DIR ($OVS_KEYS_DIR)"
        done < <(grep -o '=/keys/[^ ]*' "$svc" | sed 's/^=//')
        while read -r path; do
            [ -f "$OVS_DATA_DIR/${path#/data/}" ] || warn "$path (services.env) is not in OVS_DATA_DIR ($OVS_DATA_DIR)"
        done < <(grep -o '=/data/[^ ]*' "$svc" | sed 's/^=//')
        # The services refuse to start on a secret under 32 characters, and on an identify secret equal to the JWT one.
        local key secret
        for key in JWT_SECRET Access__IdentifySecret Gateway__EdgeSecret; do
            secret=$(value "$svc" "$key")
            [ "${#secret}" -ge 32 ] || problem "services.env $key is ${#secret} characters; the services need at least 32"
        done
        [ "$(value "$svc" JWT_SECRET)" != "$(value "$svc" Access__IdentifySecret)" ] \
            || problem "services.env Access__IdentifySecret equals JWT_SECRET: an identify token would pass as a game token"
        # The ports .env publishes and the ones services.env tells the games and the nodes must agree.
        local edge udp
        edge=$(value "$svc" Realtime__Port) udp=$(value "$svc" UDP_PORT)
        [ -z "$edge" ] || [ "$edge" = "${OVS_EDGE_PORT:-3001}" ] || problem "services.env Realtime__Port=$edge, but OVS_EDGE_PORT=${OVS_EDGE_PORT:-3001} (the games would knock on the wrong port)"
        [ -z "$udp" ] || [ "$udp" = "${OVS_RELAY_PORT:-41234}" ] || problem "services.env UDP_PORT=$udp, but OVS_RELAY_PORT=${OVS_RELAY_PORT:-41234}"
        [ "$(value "$svc" ON_DEMAND_ROLLBACK)" = "" ] || warn "services.env sets ON_DEMAND_ROLLBACK: leave it out with the relay (env/services.env.example says why)"
    fi
    if [ -f "$relay_env" ]; then
        local base
        base=$(value "$relay_env" Server__BaseUrl)
        case "$base" in
            *:"${OVS_GAME_PORT:-8000}"|*:"${OVS_GAME_PORT:-8000}"/) ;;
            *) problem "relay.env Server__BaseUrl=$base does not end in OVS_GAME_PORT (${OVS_GAME_PORT:-8000}): the relay reaches the router on this host's port" ;;
        esac
    fi
    for image in http access social lobbies web matchflow arenas matchmaking steam ws edge; do
        docker image inspect "${OVS_REGISTRY:-}ovs-$image:${OVS_TAG:-latest}" >/dev/null 2>&1 \
            || problem "image ${OVS_REGISTRY:-}ovs-$image:${OVS_TAG:-latest} is not on this host (./ovs.sh setup loads the bundle's)"
    done
    docker image inspect "${OVS_REGISTRY:-}ovs-rollback-server-csharp:${OVS_RELAY_TAG:-latest}" >/dev/null 2>&1 \
        || problem "image ${OVS_REGISTRY:-}ovs-rollback-server-csharp:${OVS_RELAY_TAG:-latest} (relay, rendezvous) is not on this host"
    if [ "$problems" -gt 0 ]; then
        echo "   $problems problem(s)"
        return 1
    fi
    echo "   ok"
}

do_up() {
    local what=${1:-all}
    case "$what" in all|dbs|services) ;; *) fail "up all|dbs|services" ;; esac
    if [ "$what" != dbs ]; then
        do_check || fail "not starting the services (the problems above)"
    fi
    if [ "$what" != services ]; then
        say "starting the stores ($project-dbs)"
        dc_dbs up -d
    fi
    if [ "$what" != dbs ]; then
        docker network inspect "$OVS_NETWORK" >/dev/null 2>&1 \
            || fail "network $OVS_NETWORK does not exist: ./ovs.sh up dbs creates it, or set OVS_NETWORK to the stores' network"
        say "starting the services ($project)"
        dc_svc up -d
    fi
    do_status
}

do_down() {
    local what=services now=()
    for arg in "$@"; do
        case "$arg" in
            --now) now=(-t 0) ;;
            all|dbs|services) what=$arg ;;
            *) fail "down [services|dbs|all] [--now]" ;;
        esac
    done
    if [ "$what" != dbs ]; then
        say "stopping the services ($project)${now:+, dropping the games on the edge}"
        dc_svc down "${now[@]}"
    fi
    if [ "$what" != services ]; then
        say "stopping the stores ($project-dbs); their volumes stay"
        dc_dbs down
    fi
}

do_status() {
    say "$project-dbs"
    dc_dbs ps --format 'table {{.Service}}\t{{.Status}}'
    say "$project"
    dc_svc ps --format 'table {{.Service}}\t{{.Status}}\t{{.Ports}}'
}

do_restore() {
    local archive="" yes=0 from=test
    while [ $# -gt 0 ]; do
        case "$1" in
            --yes) yes=1; shift ;;
            --from) from=${2:?--from needs the database name in the dump}; shift 2 ;;
            *) archive=$1; shift ;;
        esac
    done
    [ -n "$archive" ] && [ -f "$archive" ] || fail "restore ARCHIVE: a mongodump archive (.archive.zst)"
    command -v zstd >/dev/null || fail "zstd is not on PATH"
    local svc="$OVS_ENV_DIR/services.env" uri db mongo redis
    uri=$(value "$svc" MONGODB_URI)
    db=$(printf '%s' "$uri" | sed -n 's|^mongodb://[^/]*/\([^?]*\).*|\1|p')
    [ -n "$db" ] || fail "MONGODB_URI in $svc names no database (the services would use an empty default one)"
    mongo=$(dc_dbs ps -q mongo)
    redis=$(dc_dbs ps -q redis)
    [ -n "$mongo" ] && [ -n "$redis" ] || fail "the stores of $project-dbs are not running (./ovs.sh up dbs); restore writes only those"
    if [ "$yes" != 1 ]; then
        printf 'This replaces database "%s" in %s and flushes its Redis. Type the project name (%s) to go on: ' "$db" "$project-dbs" "$project"
        read -r answer
        [ "$answer" = "$project" ] || fail "not restored"
    fi
    say "restoring $(basename "$archive") into $db (the dump's '$from' database)"
    zstd -dc "$archive" | docker exec -i "$mongo" mongorestore --archive --drop --nsExclude 'admin.*' \
        --nsInclude "$from.*" --nsFrom "$from.*" --nsTo "$db.*" -u "$(value "$OVS_ENV_DIR/mongo.env" MONGO_INITDB_ROOT_USERNAME)" \
        -p "$(value "$OVS_ENV_DIR/mongo.env" MONGO_INITDB_ROOT_PASSWORD)" --authenticationDatabase admin 2>&1 \
        | grep -E 'document\(s\) restored|failed|error' | sed 's/^/   /' || true
    say "flushing Redis (nothing in it is a backup's; the services rebuild their keys)"
    docker exec "$redis" redis-cli --user "$(value "$svc" REDIS_USERNAME)" --pass "$(value "$svc" REDIS_PW)" --no-auth-warning FLUSHALL
    if [ -n "$(dc_svc ps -q)" ]; then
        say "restarting the services, so what they build at startup comes from the restored data"
        dc_svc restart >/dev/null 2>&1
    fi
    do_status
}

case "$command" in
    setup) do_setup ;;
    check) do_check ;;
    up) do_up "$@" ;;
    down) do_down "$@" ;;
    status) do_status ;;
    logs) dc_svc logs -f --tail 100 "$@" ;;
    restore) do_restore "$@" ;;
    compose)
        which=${1:-}
        shift || true
        case "$which" in dbs) dc_dbs "$@" ;; services) dc_svc "$@" ;; *) fail "compose dbs|services ARGS" ;; esac ;;
    *) usage; exit 2 ;;
esac
