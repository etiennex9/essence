#!/usr/bin/env bash
# The geocoding image's commands. The image holds Photon; its index lives in /data, which `build` fills:
#
#   serve   (the default) answers address searches and reverse lookups on port 2322 from /data/current.
#   build   downloads Photon's dump of Canada, keeps Québec's places, imports them into /data/next, then swaps it in:
#           /data/current becomes /data/previous, and /data/next becomes /data/current. A server already running keeps
#           answering from the index it loaded until it's restarted.
#   health  exits 0 when the server answers (the containers' healthcheck).
#
# An index built by one Photon version may not load in another, so /data/current records the version that built it, and
# serve refuses an index from another one: after bumping the image's Photon, run build again. Photon logs no requests:
# what users type and where they are stay out of its log.
set -euo pipefail

data=/data
# Photon's dump of Canada (OpenStreetMap's places with their addresses, from Nominatim), from the mirror photon-docker
# uses; updated about weekly, with an .md5 beside it.
dump_url=${GEOCODING_DUMP_URL:-https://r2.koalasec.org/public/north-america/canada/photon-dump-canada-master-latest.jsonl.zst}
jar=/opt/photon/photon.jar
version_file=photon-version
date_file=data-date

# The JVM's heap: serving Québec's index takes well under 1 GB; the import, a little more.
serve_heap=${GEOCODING_HEAP:-1g}
build_heap=${GEOCODING_BUILD_HEAP:-2g}
java_options=(--add-modules jdk.incubator.vector --enable-native-access=ALL-UNNAMED -Dlog4j2.disable.jmx=true)

log() {
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) essence-geocoding: $*" >&2
}

serve() {
    local current=$data/current
    if [ ! -d "$current/photon_data" ] || [ ! -f "$current/$version_file" ]; then
        log "no address index in $current: build it first (docker run --rm -v <data folder>:/data <this image> build)."
        exit 1
    fi

    local built_by
    built_by=$(<"$current/$version_file")
    if [ "$built_by" != "$PHOTON_VERSION" ]; then
        log "the address index in $current was built by Photon $built_by, and this image runs $PHOTON_VERSION: build it again."
        exit 1
    fi

    log "serving the index of $(cat "$current/$date_file" 2>/dev/null || echo "an unknown date")'s places with Photon $PHOTON_VERSION."
    exec java -Xmx"$serve_heap" "${java_options[@]}" -jar "$jar" serve -data-dir "$current" -listen-ip 0.0.0.0 -listen-port 2322 \
        -query-timeout 2
}

# The dump and its checksum, then Québec's places alone into Photon's import: the dump's header lines, and each place whose
# address is in Québec (its province's name, or its ISO code among the address's other names).
build() {
    local next=$data/next dump=$data/next/canada.jsonl.zst
    rm -rf "$next"
    mkdir -p "$next"

    # The mirror replaces the dump about once a week: an update between the checksum and the file makes them disagree, and
    # a second try gets the new pair.
    local attempt
    for attempt in 1 2; do
        log "downloading $dump_url (try $attempt)."
        curl -fsSL --retry 3 -o "$dump.md5" "$dump_url.md5"
        curl -fsSL --retry 3 -o "$dump" "$dump_url"
        if echo "$(cut -d ' ' -f 1 "$dump.md5")  $dump" | md5sum --check --status; then
            break
        fi

        if [ "$attempt" = 2 ]; then
            log "the download doesn't match its checksum: nothing changed."
            exit 1
        fi
    done

    local dated
    dated=$(zstd -dc "$dump" | head -n 1 | grep -o '"data_timestamp":"[0-9-]*' | cut -d '"' -f 4 || true)
    log "importing Québec's places from $(du -h "$dump" | cut -f 1) of Canada's, as of ${dated:-an unknown date}."
    zstd -dc "$dump" \
        | LC_ALL=C grep -F -e '"type":"NominatimDumpFile"' -e '"type":"CountryInfo"' -e '"state":"Québec"' -e '"CA-QC"' \
        | java -Xmx"$build_heap" "${java_options[@]}" -jar "$jar" import -import-file - -data-dir "$next" -languages fr,en
    echo "$PHOTON_VERSION" >"$next/$version_file"
    echo "${dated:-unknown}" >"$next/$date_file"
    rm "$dump" "$dump.md5"

    rm -rf "$data/previous"
    if [ -d "$data/current" ]; then
        mv "$data/current" "$data/previous"
    fi
    mv "$next" "$data/current"
    log "built $(du -sh "$data/current" | cut -f 1) of address index with Photon $PHOTON_VERSION; restart the server to use it."
}

health() {
    curl -fsS -o /dev/null --max-time 5 "http://127.0.0.1:2322/status"
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
