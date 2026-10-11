#!/usr/bin/env bash
# Builds what a deployment runs and packs it for the host, on a machine with this repository and Docker:
#
#   deploy/build.sh [--tag TAG] [--relay-tag TAG] [--rollback-repo DIR] [--no-relay]
#
# The services' images come from this repository (services/compose.yaml's builds), the relay and rendezvous image
# from the ovs-rollback-server repository (its build.sh image; default ../ovs-rollback-server beside this
# repository, or OVS_ROLLBACK_REPO). The result is out/openversus-deploy-<tag>.tar: this directory (compose files,
# router, env templates, ovs.sh, README) with images-<tag>.tar.zst and images.env (OVS_TAG, OVS_RELAY_TAG) inside.
# On the host: tar -xf it, cd deploy, then ./ovs.sh setup (see README.md). The public images (haproxy, mongo, redis)
# are not packed; the host pulls them. Tags default to <UTC date>-<commit>, with -dirty for uncommitted changes.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
rollback=${OVS_ROLLBACK_REPO:-$repo/../ovs-rollback-server}
tag="" relay_tag="" relay=1

say() { printf '== %s\n' "$*"; }
fail() { printf 'build.sh: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --tag) tag=${2:?--tag needs a value}; shift 2 ;;
        --relay-tag) relay_tag=${2:?--relay-tag needs a value}; shift 2 ;;
        --rollback-repo) rollback=${2:?--rollback-repo needs a directory}; shift 2 ;;
        --no-relay) relay=0; shift ;;
        -h|--help) awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"; exit 0 ;;
        *) fail "unknown option $1 (--help)" ;;
    esac
done

for tool in docker zstd python3 git tar; do
    command -v "$tool" >/dev/null || fail "$tool is not on PATH"
done

# <UTC date>-<commit>[-dirty] for a repository.
default_tag() {
    local dirty
    dirty=$(git -C "$1" status --porcelain --untracked-files=no | head -1)
    printf '%s-%s%s' "$(date -u +%Y%m%d)" "$(git -C "$1" rev-parse --short HEAD)" "${dirty:+-dirty}"
}

rev=$(git -C "$repo" rev-parse --short HEAD)
[ -n "$tag" ] || tag=$(default_tag "$repo")
case "$tag" in *-dirty) echo "note: this repository has uncommitted changes; the images include them" ;; esac

say "checking the router config against docs/routes.json"
python3 "$here/router/generate.py" --check || fail "router/haproxy.cfg is stale: run python3 deploy/router/generate.py from dotnet/"

# Compose resolves the env files and directories even to build; the builds read none of them.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
touch "$work/services.env" "$work/relay.env"
compose_build() {
    OVS_ENV_DIR=$work OVS_DATA_DIR=$work OVS_KEYS_DIR=$work OVS_RUN_DIR=$work OVS_LOG_DIR=$work \
        OVS_REGISTRY="" OVS_TAG=$tag OVS_SOURCE_REVISION=$rev docker compose -f "$here/services/compose.yaml" "$@"
}

say "building the services' images (tag $tag)"
compose_build build
mapfile -t images < <(compose_build config --images | grep -E "^ovs-[a-z0-9-]+:$tag\$" | sort -u)
[ ${#images[@]} -gt 0 ] || fail "no ovs-*:$tag image in the compose file's list"

if [ "$relay" = 1 ]; then
    [ -x "$rollback/build.sh" ] || fail "no rollback repository at $rollback (--rollback-repo DIR, or --no-relay to pack without the relay image)"
    [ -n "$relay_tag" ] || relay_tag=$(default_tag "$rollback")
    say "building the relay and rendezvous image (tag $relay_tag, from $rollback)"
    (cd "$rollback" && ./build.sh image "$relay_tag")
    images+=("ovs-rollback-server-csharp:$relay_tag")
else
    echo "note: --no-relay: the bundle has no relay image; the host needs ovs-rollback-server-csharp:\${OVS_RELAY_TAG} already"
fi

out="$here/out"
mkdir -p "$out"
stage="$work/stage/deploy"
mkdir -p "$stage"
# This directory as it is in the tree, without what never travels: a local .env and earlier bundles.
tar -C "$here" --exclude=./out --exclude=./.env --exclude='./images-*.tar.zst' --exclude=./images.env -cf - . | tar -C "$stage" -xf -

say "saving ${#images[@]} images"
printf '   %s\n' "${images[@]}"
docker save "${images[@]}" | zstd -T0 -q -o "$stage/images-$tag.tar.zst"
printf 'OVS_TAG=%s\nOVS_RELAY_TAG=%s\n' "$tag" "$relay_tag" > "$stage/images.env"

bundle="$out/openversus-deploy-$tag.tar"
tar -C "$work/stage" -cf "$bundle" deploy
say "wrote $bundle ($(du -h "$bundle" | cut -f1))"
echo "   on the host: tar -xf $(basename "$bundle") && cd deploy && ./ovs.sh setup"
