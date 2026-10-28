#!/usr/bin/env bash
# The geocoding image's commands. The image holds Photon; its index of Québec's places lives in /data, which it builds
# itself:
#
#   run     (the default) the supervisor (supervisor.sh): builds the index when there's none, or when it was built by
#           another Photon version, serves it on port 2322, refreshes it every REFRESH_DAYS days (30) when the dump
#           changed, and reports what it's doing at GET :9090/status.
#   health  exits 0 while the supervisor runs (the containers' healthcheck), whether it builds, serves or waits.
#   refresh rebuilds the index now, while the server keeps answering from the current one.
#
# Photon logs no requests: what users type and where they are stay out of its log.
set -uo pipefail

SERVICE=geocoding
ENTRYPOINT=$(readlink -f "$0")
# shellcheck source=../Essence.Supervisor/supervisor.sh
source /usr/local/lib/essence/supervisor.sh

# Photon's dump of Canada (OpenStreetMap's places with their addresses, from Nominatim), from the mirror photon-docker
# uses; updated about weekly, with an .md5 beside it.
dump_url=${GEOCODING_DUMP_URL:-https://r2.koalasec.org/public/north-america/canada/photon-dump-canada-master-latest.jsonl.zst}
jar=/opt/photon/photon.jar

# The JVM's heap: serving Québec's index takes well under 1 GB; the import, a little more.
serve_heap=${GEOCODING_HEAP:-1g}
build_heap=${GEOCODING_BUILD_HEAP:-2g}
java_options=(--add-modules jdk.incubator.vector --enable-native-access=ALL-UNNAMED -Dlog4j2.disable.jmx=true)

data_version() { echo "Photon $PHOTON_VERSION"; }

# What the index keeps besides Photon's version: the languages and the filter below.
data_rules() { echo "fr,en; state Québec or CA-QC"; }

# The dump's checksum, which changes with the dump.
source_id() {
    curl -fsSL --max-time 30 "$dump_url.md5" | cut -d ' ' -f 1 | grep -E '^[0-9a-f]{32}$'
}

# The dump and its checksum, then Québec's places alone into Photon's import: the dump's header lines, and each place
# whose address is in Québec (its province's name, or its ISO code among the address's other names).
build_next() {
    local next=$DATA/next dump=$DATA/next/canada.jsonl.zst dated
    stage downloading 0 40
    curl -fsSL --retry 3 --max-time 60 -o "$dump.md5" "$dump_url.md5" || { echo "the checksum's download failed" >"$RUN/error"; return 1; }
    download "$dump_url" "$dump" || return 1

    stage verifying 40 43
    # The mirror replacing the dump between the two downloads makes them disagree: the next try gets a matching pair.
    if ! echo "$(cut -d ' ' -f 1 "$dump.md5")  $dump" | md5sum --check --status; then
        echo "the download doesn't match its checksum" >"$RUN/error"
        return 1
    fi

    dated=$(zstd -dc "$dump" 2>/dev/null | head -n 1 | grep -o '"data_timestamp":"[0-9-]*' | cut -d '"' -f 4)
    stage importing 43 99
    # pv reports how much of the dump has been read, which is how far the import is.
    pv -n -s "$(stat -c %s "$dump")" "$dump" 2>"$RUN/build.pv" \
        | zstd -dc \
        | LC_ALL=C grep -F -e '"type":"NominatimDumpFile"' -e '"type":"CountryInfo"' -e '"state":"Québec"' -e '"CA-QC"' \
        | java -Xmx"$build_heap" "${java_options[@]}" -jar "$jar" import -import-file - -data-dir "$next" -languages fr,en \
            2>&1 | tee -a "$RUN/build.log" >&2
    local codes=("${PIPESTATUS[@]}")
    if [ "${codes[3]}" -ne 0 ] || [ "${codes[1]}" -ne 0 ]; then
        echo "Photon's import failed (exit code ${codes[3]})" >"$RUN/error"
        return 1
    fi

    # A dump the filter no longer finds Québec's places in (its format changed) makes an index with next to nothing in
    # it: under half the places of the current index, or under 100,000, it isn't served. Photon says how many it has; if
    # it doesn't any more, the index's size tells.
    local documents least
    documents=$(sed -n 's/.*Finished import of \([0-9]*\) photon documents.*/\1/p' "$RUN/build.log" | tail -n 1)
    least=$(($(grep -sxE '[0-9]+' "$DATA/current/documents" || echo 0) / 2))
    [ "$least" -lt 100000 ] && least=100000
    if [ -n "$documents" ] && [ "$documents" -lt "$least" ]; then
        echo "Photon's import found only $documents places in Québec" >"$RUN/error"
        return 1
    elif [ -z "$documents" ] && [ "$(du -sm "$next/photon_data" | cut -f 1)" -lt 200 ]; then
        echo "Photon's import made an index of under 200 MB" >"$RUN/error"
        return 1
    fi

    [ -n "$documents" ] && echo "$documents" >"$next/documents"
    cut -d ' ' -f 1 "$dump.md5" >"$next/source-id"
    rm -f "$dump" "$dump.md5"
    data_version >"$next/version"
    data_rules >"$next/rules"
    echo "${dated:-unknown}" >"$next/data-date"
    date +%s >"$next/built-at"
    touch "$next/complete"
}

stage_progress() {
    [ "$1" = importing ] && tail -n 1 "$RUN/build.pv" 2>/dev/null | tr -cd '0-9'
}

server_start() {
    java -Xmx"$serve_heap" "${java_options[@]}" -jar "$jar" serve -data-dir "$DATA/current" -listen-ip 0.0.0.0 -listen-port 2322 \
        -query-timeout 2 &
}

server_ready() {
    curl -fsS -o /dev/null --max-time 3 "http://127.0.0.1:2322/status"
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
