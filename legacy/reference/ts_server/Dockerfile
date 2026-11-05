# syntax=docker/dockerfile:1
FROM node:24-trixie AS builder

WORKDIR /app
COPY . .
RUN npm ci || npm install; npm run build

# Prod image
FROM node:24-trixie

RUN apt update -y \
 && apt upgrade -y \
 && apt install vim net-tools tcpdump -y \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=builder /app/build ./build
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/package.json ./package.json
COPY --from=builder /app/package-lock.json ./package-lock.json
COPY src/static ./build/src/static
COPY scripts/ /startup/

# Expose Websocket, HTTP, and UDP rollback server ports.
# They shouldn't all be listening externally in every stack, but
# exposing them all in the Dockerfile allows reuse of the same image
# for every server.
EXPOSE 3000 8000 41234/udp

# Just a pre-built static binary stuffed into the container for emergency use
# Or if not running separate rollback servers. Built from the source available here:
#
# https://github.com/openversus/ovs-udp-server
#
COPY rollback-server ./
COPY entrypoint.sh /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
