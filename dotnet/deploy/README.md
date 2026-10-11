# Deploying the OpenVersus server infrastructure via Docker Compose

Two Compose projects: `dbs/` (MongoDB, Redis, the `ovs` network) and `services/` (the C# services, the HAProxy router
in front of them, the rollback relay). The stores are their own project so that taking the services down can never
take the data with it.

## The short way: build.sh and ovs.sh

On a machine with this repository, the ovs-rollback-server repository beside it, and Docker:

```
deploy/build.sh            # the services' images + the relay image -> deploy/out/openversus-deploy-<tag>.tar
```

The tarball is this directory with the images inside (`images-<tag>.tar.zst`, and `images.env` naming the tags), so
the host needs neither the source nor the SDK. On the host:

```
tar -xf openversus-deploy-<tag>.tar && cd deploy
cp env/deploy.env.example .env      # first time: directories, ports, OVS_PROJECT, OVS_NETWORK, OVS_RELAY_ADDRESS
./ovs.sh setup                      # directories, env templates into OVS_ENV_DIR (never overwritten), images, checks
#   edit the files setup copied (services.env, relay.env, mongo.env, redis.conf), put the node config key pair in
#   OVS_KEYS_DIR and the ban files and name lists in OVS_DATA_DIR, then:
./ovs.sh check                      # what would stop a start: template values, short secrets, missing keys, ports
./ovs.sh up                         # the stores, then the services (`up services` when the stores run elsewhere)
./ovs.sh restore prod.archive.zst   # a prod mongodump (the TS `test` database) into the stores, Redis flushed
./ovs.sh status | logs [service] | down [services|dbs|all] [--now]
```

An update is the same: build a new tarball, extract it over the old directory (its `.env` stays), `./ovs.sh setup`
(loads the new images; the env files are left alone), `./ovs.sh up`. `check` compares what `.env` publishes with
what `services.env` and `relay.env` tell the games and the nodes (the edge port, the relay port, the relay's way back
to the router), so a host on non-default ports says so before anything starts. `./ovs.sh compose dbs|services ...`
runs any other Compose command with the same settings (`./ovs.sh compose services exec matchflow ovsctl settings
list`).

Two stacks on one host (a testing one beside prod) need their own `OVS_PROJECT` (the project and volume names),
`OVS_NETWORK`, ports and directories.

## Layout on the host

```
/opt/openversus/              a checkout of the repository (or just its dotnet/deploy/ directory)
/etc/openversus/              OVS_ENV_DIR: services.env, mongo.env, relay.env, redis.conf (from env/*.example)
/var/lib/openversus/data/     OVS_DATA_DIR: the ban files and the yaml name lists (see env/services.env.example)
/var/lib/openversus/keys/     OVS_KEYS_DIR: the node config signing key pair
/run/openversus/              OVS_RUN_DIR: the services' control sockets, for ovsctl on the host
/var/log/openversus/          OVS_LOG_DIR: a daily log file per service, beside `docker compose logs`
```

The services run as `OVS_UID:OVS_GID`, the host user that owns those directories (an `openversus` user, say), so the
bind mounts need no ownership tricks and that user, or its group, runs ovsctl.

Put the directory variables, `OVS_UID`/`OVS_GID`, `OVS_REGISTRY`/`OVS_TAG`, the public ports and `OVS_RELAY_ADDRESS`
in one `.env` and pass it to both projects with `--env-file` (Compose otherwise reads only the `.env` beside the compose
file it was given, so `dbs/` would not see `services/.env`). Nothing under `deploy/` holds a secret or a host name; the
`.env` and `OVS_ENV_DIR` do.

`OVS_RELAY_ADDRESS` is required: this host's public address, which the rendezvous hands every P2P node as the relay to
fall back to. Left out it is 127.0.0.1, which only a node on this host can reach: everyone else's fallback goes nowhere.
(It defaults to that rather than to any public relay, so a deployment never uses someone else's by accident.)

## Bringing it up

```
docker compose --env-file .env -f dbs/compose.yaml up -d
docker compose --env-file .env -f services/compose.yaml up -d
docker compose --env-file .env -f services/compose.yaml ps   # healthy = ovsctl health --probe says the service is ready
```

The game's HTTP API is on `OVS_GAME_PORT` (8000) through the router; its websocket on `OVS_EDGE_PORT` (3001) through
the edge; the relay on UDP `OVS_RELAY_PORT` (41234) and the rendezvous on UDP `OVS_RENDEZVOUS_PORT` (41235), both on
the host's own network. Everything else stays on `ovs`.

## Moving onto an existing host

The dbs project is not needed where MongoDB and Redis already run: set `OVS_NETWORK` to their network and point
`MONGODB_URI` and `REDIS*` at them, then `./ovs.sh up services`. The TS server kept its data in the database named
`test`; `MONGODB_URI`'s path must say so, or the services start from an empty database.

## A bench

A development bench adds an override file of its own outside the repository: the game-facing ports on localhost. The
game's entry is the router, as in a deployment.

## Updating

Images are tagged (`OVS_TAG`). A new build goes out as `docker compose -f services/compose.yaml pull && docker compose
-f services/compose.yaml up -d`; one service alone with `up -d --no-deps <service>`. Without a registry, build on the
host: `docker compose -f services/compose.yaml build` (the whole set builds in about a minute) then `up -d`.

The edge keeps its games through an update: `stop_grace_period` is hours, and a drained edge exits as soon as its last
game leaves. That also means `docker compose down` or `stop` waits on the edge while games are attached; `stop -t 0
edge` (or `down -t 0`) drops them deliberately.

The relay's and the rendezvous's image comes from the ovs-rollback-server repository (`build.sh image <tag>`), tagged as
`OVS_RELAY_TAG`.

## The router

`router/haproxy.cfg` is generated from `docs/routes.json` by `router/generate.py`: every path goes to the service that
owns it, exact paths before templates, anything unlisted to the http service. The router writes the client's address
into X-Real-IP and X-Forwarded-For itself, dropping whatever the client sent there (bans key off that address), and
answers 404 for the services' `/health/*` endpoints, which are for Docker and the router alone. Regenerate it when routes.json changes
(`python3 deploy/router/generate.py`; `--check` says whether it is current).

## ovsctl from the host

Each service's control socket appears in `OVS_RUN_DIR` (`http.sock`, `access.sock`, ...). `ovsctl --socket
/run/openversus/web.sock settings list`, or `docker compose exec web ovsctl ...` inside the container. The sockets belong to
`OVS_UID:OVS_GID`; who may use them is plain POSIX permission on the socket files.

## Logs

`docker compose logs -f <service>` (the console sink, json-file, rotated), and a daily file per service in
`OVS_LOG_DIR` (the File sink configured in services.env).
