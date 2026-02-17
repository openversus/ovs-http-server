#!/usr/bin/env bash

# Change this to your desired directory for storing OpenVersus
# config files, environment files, and the docker-compose.yml file.
# The config files you save in this directory are referenced as
# volumes in the docker-compose.yml file.
OVSDIR="/opt/docker/openversus"

# This script assumes that you are running it from the root of
# the cloned git repo, so change the value of SOURCEDIR below if
# you're not.
SOURCEDIR=$(pwd)

if [ ! -d "$OVSDIR" ]; then
    mkdir -p "$OVSDIR" || die "Failed to create directory $OVSDIR"
fi

cd "$OVSDIR" || die "Failed to change directory to $OVSDIR"

# Comment the line below if you want to keep your existing containers running while building the new image.
# You'll need to give the newly-built image a different tag, though.
docker compose down
cd "$SOURCEDIR" || die "Failed to change directory to $SOURCEDIR"

docker pull node:24-trixie
docker run --rm -it --entrypoint /bin/bash -v${SOURCEDIR}:/tmp/ovs node:24-trixie -c "cd /tmp/ovs; npm install; npm run build"

# Or whatever you want to call it and tag the image with.
docker build -t "openversus:latest" .

cd "$OVSDIR" || die "Failed to change directory to $OVSDIR"
echo "Done building OpenVersus image at $(date --iso-8601='ns' | cut -d+ -f1)"

