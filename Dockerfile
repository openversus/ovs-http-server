# syntax=docker/dockerfile:1
FROM node:24-trixie

WORKDIR /app

# Copy only production dependencies
COPY package*.json ./
RUN npm ci --omit=dev || npm install --omit=dev --legacy-peer-deps

# Copy built files and any other needed files
COPY build ./build
COPY scripts/ /startup/

# Expose port (change if your app uses a different port)
EXPOSE 3000 8000 9102 41234/udp

# Just a pre-built static binary stuffed into the container for emergency use
# Or if not running separate rollback servers. Built from the source available here:
#
# https://github.com/openversus/ovs-udp-server
#
COPY rollback-server ./
COPY entrypoint.sh /entrypoint.sh

RUN apt update && \
    apt install vim net-tools tcpdump -y && \
    rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["/entrypoint.sh"]
