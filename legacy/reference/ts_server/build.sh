#!/usr/bin/env bash

function die
{
  local message=$1
  [ -z "$message" ] && message="Died"
  echo "${BASH_SOURCE[1]}: line ${BASH_LINENO[0]}: ${FUNCNAME[1]}: $message." >&2
  exit 1
}

# Change this to your desired directory for storing OpenVersus
# config files, environment files, and the docker-compose.yml file.
# The config files you save in this directory are referenced as
# volumes in the docker-compose.yml file.
OVS_ROOT_DIR="/opt/docker/openversus"

# This script assumes that you are running it from the root of
# the cloned git repo, so change the value of SOURCEDIR below if
# you're not.
SOURCEDIR=$(pwd)

if [ ! -d "$OVS_ROOT_DIR" ]; then
    mkdir -p "$OVS_ROOT_DIR" || die "Failed to create directory $OVS_ROOT_DIR"
    cd "${SOURCEDIR}/examples" || die "Failed to cd to exmaples directory"
    cp -a . "$OVS_ROOT_DIR/" || die "Failed to copy examples to $OVS_ROOT_DIR"
    find "$OVS_ROOT_DIR" -type f -iname '*.sh' -exec chmod ug+x {} \; || die "Failed to set execute permissions on shell scripts in $OVS_ROOT_DIR"

    echo "Created directory $OVS_ROOT_DIR and copied example configuration files. Please review and edit these files as needed before running this script again to build the Docker image."
fi

cd "$OVS_ROOT_DIR" || die "Failed to change directory to $OVS_ROOT_DIR"

# Comment the line below if you want to keep your existing containers running while building the new image.
# You'll need to give the newly-built image a different tag, though.
docker compose down
cd "$SOURCEDIR" || die "Failed to change directory to $SOURCEDIR"

# Pull the base image to speed up the build process. This is optional, but it can save time if the base image is already cached locally.
docker pull node:24-trixie

# Or whatever you want to call it and tag the image with.
docker build -t "openversus:latest" .

cd "$OVS_ROOT_DIR" || die "Failed to change directory to $OVS_ROOT_DIR"
echo "Done building OpenVersus image at $(date --iso-8601='ns' | cut -d+ -f1)"
printf "\n"
echo "Run the script "start.sh" in this directory ($OVS_ROOT_DIR) to start the OpenVersus server stacks using Docker Compose."
echo "To safely stop the stacks, run the script "stop.sh" in this directory ($OVS_ROOT_DIR)."
printf "\n"
