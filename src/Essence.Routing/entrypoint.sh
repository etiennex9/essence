#!/usr/bin/env bash
# The routing image's commands. The image holds OSRM's programs; the map data lives in /data, which `build` fills:
#
#   serve   (the default) answers routes and distance tables on port 5000 from /data/current.
#   build   downloads Québec's roads from Geofabrik, processes them into /data/next, then swaps it in: /data/current
#           becomes /data/previous, and /data/next becomes /data/current. A server already running keeps answering
#           from the data it loaded until it's restarted.
#   health  exits 0 when the server answers a lookup (the containers' healthcheck).
#
# Data built by one OSRM version may not load in another, so /data/current records the version that built it, and serve
# refuses data from another one: after bumping the image's OSRM, run build again. It also records the profile it was
# built with (essence-car.lua), and serve warns when the image's differs: the data still loads, with the old rules.
set -euo pipefail

data=/data
pbf_url=${ROUTING_PBF_URL:-https://download.geofabrik.de/north-america/canada/quebec-latest.osm.pbf}
version_file=osrm-version
profile_file=profile-sha256
profile=/opt/essence-car.lua

log() {
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) essence-routing: $*" >&2
}

osrm_version() {
    osrm-routed --version
}

profile_hash() {
    sha256sum "$profile" | cut -d ' ' -f 1
}

serve() {
    local current=$data/current
    if [ ! -f "$current/quebec.osrm.mldgr" ] || [ ! -f "$current/$version_file" ]; then
        log "no routing data in $current: build it first (docker run --rm -v <data folder>:/data <this image> build)."
        exit 1
    fi

    local built_by running
    built_by=$(<"$current/$version_file")
    running=$(osrm_version)
    if [ "$built_by" != "$running" ]; then
        log "the routing data in $current was built by OSRM $built_by, and this image runs $running: build it again."
        exit 1
    fi

    if [ "$(cat "$current/$profile_file" 2>/dev/null)" != "$(profile_hash)" ]; then
        log "warning: the routing data in $current was built with another car profile than this image's: build it again to use the new one."
    fi

    log "serving the data built $(date -u -r "$current/$version_file" +%Y-%m-%d) by OSRM $running."
    # WARNING: at INFO, osrm-routed logs every request's path, and with it where users are.
    exec osrm-routed --algorithm mld --max-table-size 200 -l WARNING --ip 0.0.0.0 --port 5000 "$current/quebec.osrm"
}

# The extract, its checksum from Geofabrik, then OSRM's three steps for the MLD algorithm.
build() {
    local next=$data/next pbf=$data/next/quebec.osm.pbf
    rm -rf "$next"
    mkdir -p "$next"

    # Geofabrik replaces the file once a day: an update between the checksum and the file makes them disagree, and a
    # second try gets the new pair.
    local attempt
    for attempt in 1 2; do
        log "downloading $pbf_url (try $attempt)."
        curl -fsSL --retry 3 -o "$pbf.md5" "$pbf_url.md5"
        curl -fsSL --retry 3 -o "$pbf" "$pbf_url"
        if echo "$(cut -d ' ' -f 1 "$pbf.md5")  $pbf" | md5sum --check --status; then
            break
        fi

        if [ "$attempt" = 2 ]; then
            log "the download doesn't match its checksum: nothing changed."
            exit 1
        fi
    done

    log "processing $(du -h "$pbf" | cut -f 1) of roads."
    osrm-extract -p "$profile" "$pbf"
    osrm-partition "$next/quebec.osrm"
    osrm-customize "$next/quebec.osrm"
    osrm_version >"$next/$version_file"
    profile_hash >"$next/$profile_file"
    rm "$pbf" "$pbf.md5"

    rm -rf "$data/previous"
    if [ -d "$data/current" ]; then
        mv "$data/current" "$data/previous"
    fi
    mv "$next" "$data/current"
    log "built $(du -sh "$data/current" | cut -f 1) of routing data with OSRM $(osrm_version); restart the server to use it."
}

health() {
    curl -fsS -o /dev/null --max-time 5 "http://127.0.0.1:5000/nearest/v1/driving/-73.5673,45.5017"
}

case "${1:-serve}" in
    serve) serve ;;
    build) build ;;
    health) health ;;
    *)
        log "unknown command \"$1\": serve, build or health."
        exit 2
        ;;
esac
