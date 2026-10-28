#!/usr/bin/env bash
# The routing image's commands. The image holds OSRM's programs; the data lives in /data, which it builds itself:
#
#   run     (the default) the supervisor (supervisor.sh): builds Québec's road data when there's none, or when it was
#           built by another OSRM version, serves it on port 5000, refreshes it every REFRESH_DAYS days (30) when
#           Geofabrik's extract changed, and reports what it's doing at GET :9090/status.
#   health  exits 0 while the supervisor runs (the containers' healthcheck), whether it builds, serves or waits.
#   refresh rebuilds the data now, while the server keeps answering from the current data.
#
# The data records the OSRM version and the car profile (essence-car.lua) it was built with: data from another OSRM
# version isn't served (it may not load) and is rebuilt; data built with another profile is served, and rebuilt.
set -uo pipefail

SERVICE=routing
ENTRYPOINT=$(readlink -f "$0")
# shellcheck source=../Essence.Supervisor/supervisor.sh
source /usr/local/lib/essence/supervisor.sh

pbf_url=${ROUTING_PBF_URL:-https://download.geofabrik.de/north-america/canada/quebec-latest.osm.pbf}
profile=/opt/essence-car.lua

data_version() { echo "OSRM $(osrm-routed --version)"; }

data_rules() { sha256sum "$profile" | cut -d ' ' -f 1; }

# The extract's Last-Modified: Geofabrik replaces it once a day. quebec-latest redirects to the day's file, which has it.
source_id() {
    curl -fsSIL --max-time 30 "$pbf_url" | tr -d '\r' | sed -n 's/^[Ll]ast-[Mm]odified: *//p' | tail -n 1 | grep .
}

# Geofabrik's extract and its checksum, cut down to what a car's routing reads (osmium: roads, ferries, movable bridges,
# turn restrictions and maneuvers, with their nodes; the same routes, and half the memory), then OSRM's three steps for
# the MLD algorithm. osrm-extract needs about 3.2 GB of RAM for Québec this way, 6.6 GB on the whole extract.
build_next() {
    local next=$DATA/next pbf=$DATA/next/quebec-latest.osm.pbf id
    stage downloading 0 50
    id=$(source_id) || { echo "the extract's server doesn't answer" >"$RUN/error"; return 1; }
    curl -fsSL --retry 3 --max-time 60 -o "$pbf.md5" "$pbf_url.md5" || { echo "the checksum's download failed" >"$RUN/error"; return 1; }
    download "$pbf_url" "$pbf" || return 1

    stage verifying 50 53
    # Geofabrik replacing the file between the two downloads makes them disagree: the next try gets a matching pair.
    if ! echo "$(cut -d ' ' -f 1 "$pbf.md5")  $pbf" | md5sum --check --status; then
        echo "the download doesn't match its checksum" >"$RUN/error"
        return 1
    fi

    # The data's date is the extract's own timestamp (the evening before Geofabrik publishes it).
    osmium fileinfo -g header.option.timestamp "$pbf" 2>/dev/null | cut -c 1-10 >"$next/data-date"
    stage filtering 53 60
    run_logged "osmium" osmium tags-filter "$pbf" w/highway w/route=ferry,shuttle_train w/bridge=movable \
        r/type=restriction,maneuver -o "$next/quebec.osm.pbf" --overwrite || return 1
    rm -f "$pbf" "$pbf.md5"

    stage extracting 60 88
    run_logged "osrm-extract" osrm-extract -p "$profile" "$next/quebec.osm.pbf" || return 1
    rm -f "$next/quebec.osm.pbf"
    stage partitioning 88 95
    run_logged "osrm-partition" osrm-partition "$next/quebec.osrm" || return 1
    stage customizing 95 99
    run_logged "osrm-customize" osrm-customize "$next/quebec.osrm" || return 1

    data_version >"$next/version"
    data_rules >"$next/rules"
    echo "$id" >"$next/source-id"
    [ -s "$next/data-date" ] || date -u -d "$id" +%Y-%m-%d >"$next/data-date"
    date +%s >"$next/built-at"
    touch "$next/complete"
}

# osrm-extract says where it is: parsing is most of its time, then a few steps that each print 10 % … 100 %.
stage_progress() {
    [ "$1" = extracting ] || return 0
    local last
    last=$(grep -oE 'Parsing in progress|Parsing finished|extraction finished|Generating edge-expanded graph representation|Generating edge expanded nodes|Generating edge-expanded edges|Generating guidance turns|RAM: peak' "$2" | tail -n 1)
    case "$last" in
        "Parsing in progress") echo 5 ;;
        "Parsing finished") echo 55 ;;
        "extraction finished") echo 62 ;;
        "Generating edge-expanded graph representation") echo 65 ;;
        "Generating edge expanded nodes") echo 75 ;;
        "Generating edge-expanded edges") echo 80 ;;
        "Generating guidance turns") echo 90 ;;
        "RAM: peak") echo 100 ;;
        *) echo 0 ;;
    esac
}

# WARNING: at INFO, osrm-routed logs every request's path, and with it where users are.
server_start() {
    osrm-routed --algorithm mld --max-table-size 200 -l WARNING --ip 0.0.0.0 --port 5000 "$DATA/current/quebec.osrm" &
}

server_ready() {
    curl -fs -o /dev/null --max-time 3 "http://127.0.0.1:5000/nearest/v1/driving/-73.5673,45.5017"
}

case "${1:-run}" in
    run) supervise ;;
    health) health ;;
    refresh) refresh ;;
    _build) build_next ;;
    *)
        log "unknown command \"$1\": run, health or refresh."
        exit 2
        ;;
esac
