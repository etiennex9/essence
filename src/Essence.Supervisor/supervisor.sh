#!/usr/bin/env bash
# The supervisor of a data service: essence-routing (OSRM and Québec's roads) or essence-geocoding (Photon and Québec's
# places). Sourced by the image's entrypoint, which defines the service's hooks (below), then runs `supervise`. It never
# exits because data is missing, outdated, built by another version, or a download failed:
#
# - With no data (first start, or data from another OSRM or Photon version), it builds the data, then starts the server.
# - It refreshes the data on its own every REFRESH_DAYS days (30 by default; 0 never), first checking that the source
#   changed, and right away after `<command> refresh`. It builds into /data/next while the server answers from
#   /data/current, swaps them, restarts the server, and keeps the previous data until the new data loads.
# - A failed build is retried with a growing delay (1 minute, doubling, at most 6 hours); a partial build is thrown away
#   when the container restarts, and a download picks up where it stopped.
# - Its state, the build's stage and progress, the data's date, the last error and what's next are served as JSON at
#   GET /status on STATUS_PORT (9090), with a heartbeat the healthcheck reads: the container is healthy while the
#   supervisor runs, whether it builds, serves or waits, and unhealthy only when it's stuck.
#
# Hooks the entrypoint defines:
#   data_version      what the data must have been built by; data built by another is rebuilt before it's served
#   data_rules        what else the data depends on (a profile): data built with others is served, and rebuilt soon
#   source_id         prints an id of the source's current version (a date, a checksum); fails when it can't be had
#   build_next        builds into $DATA/next, calling stage, download and run_logged; fails with a message in $RUN/error
#   stage_progress    given a stage and its log, prints the stage's percent when the log tells it
#   server_start      starts the server in the background, from $DATA/current; prints nothing
#   server_ready      exits 0 when the server answers
set -uo pipefail

DATA=${DATA:-/data}
RUN=${RUN:-/tmp/essence}
STATUS_PORT=${STATUS_PORT:-9090}
REFRESH_DAYS=${REFRESH_DAYS:-30}
TICK=5

# The server, the build and the status server, by process id.
server_pid=
build_pid=
httpd_pid=

# What the status says, besides what's derived.
serving=false
build_kind=
build_started=
last_error=
failures=0
next_attempt=0
next_refresh=0
server_failures=0
next_server_start=0
swap_deadline=0
last_probe=0

log() {
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $SERVICE: $*" >&2
}

now() { date +%s; }

iso() { [ -n "${1:-}" ] && [ "$1" -gt 0 ] && date -u -d "@$1" +%Y-%m-%dT%H:%M:%SZ || true; }

# A JSON string, or null when empty. Messages are this script's own, so escaping quotes, backslashes and controls is enough.
json_string() {
    if [ -z "${1:-}" ]; then
        printf null
    else
        printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr '\n\r\t' '   ')"
    fi
}

json_number() { [ -n "${1:-}" ] && printf '%s' "$1" || printf null; }

# ---- Data on disk ----

# The data in a folder is complete and was built by this image's version.
data_usable() {
    [ -f "$1/complete" ] && [ "$(cat "$1/version" 2>/dev/null)" = "${VERSION:-$(data_version)}" ]
}

meta() { cat "$DATA/current/$1" 2>/dev/null || true; }

# After a restart: a build that was under way is thrown away, and a swap that was under way is finished or undone.
recover() {
    mkdir -p "$DATA" "$RUN"
    if [ ! -d "$DATA/current" ] && [ -f "$DATA/next/complete" ]; then
        log "finishing a swap a restart interrupted."
        mv "$DATA/next" "$DATA/current"
    elif [ ! -d "$DATA/current" ] && [ -d "$DATA/previous" ]; then
        log "restoring the previous data, as a restart interrupted a swap."
        mv "$DATA/previous" "$DATA/current"
    fi

    if [ -d "$DATA/next" ]; then
        log "throwing away a build a restart interrupted."
        rm -rf "$DATA/next"
    fi

    # Kept until new data loaded; a restart in between keeps the current data, which is the new one.
    if [ -d "$DATA/current" ] && [ -d "$DATA/previous" ]; then
        rm -rf "$DATA/previous"
    fi

    rm -f "$RUN"/build.* "$RUN/error"
    schedule_refresh_from_data
}

schedule_refresh_from_data() {
    local built
    built=$(meta built-at)
    if [ "$REFRESH_DAYS" -gt 0 ] && [ -n "$built" ]; then
        next_refresh=$((built + REFRESH_DAYS * 86400))
    else
        next_refresh=0
    fi
}

# ---- The build, in the background ----

# Called by build_next: the stage under way, and the share of the whole build it covers (from, to percent).
stage() {
    printf '%s\n' "$1" >"$RUN/build.stage"
    printf '%s %s\n' "$2" "$3" >"$RUN/build.range"
    : >"$RUN/build.log"
    echo 0 >"$RUN/build.progress"
    log "build: $1."
}

# Called by build_next: downloads $1 to $2, resuming a download a restart interrupted, with its progress by size.
download() {
    local url=$1 target=$2 part="$DATA/downloads/$(basename "$2").part" size
    mkdir -p "$DATA/downloads"
    size=$(curl -fsSLI --max-time 30 "$url" | tr -d '\r' | awk 'tolower($1) == "content-length:" { n = $2 } END { print n }')
    echo "${size:-0}" >"$RUN/build.size"
    echo "$part" >"$RUN/build.file"
    # Abort a stalled transfer (under 10 kB/s for 2 minutes): the build is retried later.
    if ! curl -fsSL --retry 3 --speed-limit 10000 --speed-time 120 -C - -o "$part" "$url"; then
        # A file that changed upstream can't be resumed: start over next time.
        rm -f "$part"
        echo "the download of $(basename "$2") failed" >"$RUN/error"
        return 1
    fi

    rm -f "$RUN/build.file" "$RUN/build.size"
    mv "$part" "$target"
}

# Called by build_next: runs a step with its output in the stage's log (and the container's), failing with a message.
run_logged() {
    local what=$1
    shift
    "$@" 2>&1 | tee -a "$RUN/build.log" >&2
    local code=${PIPESTATUS[0]}
    if [ "$code" -ne 0 ]; then
        echo "$what failed (exit code $code)" >"$RUN/error"
        return 1
    fi
}

start_build() {
    build_kind=$1
    build_started=$(now)
    rm -rf "$DATA/next"
    mkdir -p "$DATA/next"
    rm -f "$RUN"/build.* "$RUN/error"
    log "starting a build ($build_kind)."
    # In its own process group, so that stopping the container stops its programs too.
    setsid "$ENTRYPOINT" _build &
    build_pid=$!
}

# The build's share done, in percent of the whole: its stage's range, and how far into it the stage is.
build_percent() {
    local range from to progress=0 size file got
    range=$(cat "$RUN/build.range" 2>/dev/null) || return 0
    read -r from to <<<"$range"
    file=$(cat "$RUN/build.file" 2>/dev/null || true)
    size=$(cat "$RUN/build.size" 2>/dev/null || echo 0)
    if [ -n "$file" ] && [ "$size" -gt 0 ]; then
        got=$(stat -c %s "$file" 2>/dev/null || echo 0)
        progress=$((got * 100 / size))
    else
        progress=$(stage_progress "$(cat "$RUN/build.stage" 2>/dev/null)" "$RUN/build.log" 2>/dev/null || true)
        progress=${progress:-$(cat "$RUN/build.progress" 2>/dev/null || echo 0)}
    fi

    [ "$progress" -gt 100 ] 2>/dev/null && progress=100
    echo $((from + (to - from) * ${progress:-0} / 100))
}

# The build ended: swap its data in, or count the failure and wait.
finish_build() {
    local code=$1
    build_pid=
    if [ "$code" -eq 0 ] && [ -f "$DATA/next/complete" ]; then
        log "build done: $(du -sh "$DATA/next" | cut -f 1) of data from $(cat "$DATA/next/data-date" 2>/dev/null)."
        stop_server
        rm -rf "$DATA/previous"
        [ -d "$DATA/current" ] && mv "$DATA/current" "$DATA/previous"
        mv "$DATA/next" "$DATA/current"
        # Asked for, and done: a refresh asked for and interrupted by a restart starts again.
        rm -rf "$DATA/downloads" "$DATA/refresh-now"
        server_failures=0
        next_server_start=0
        swap_deadline=$(($(now) + 600))
        return
    fi

    last_error=$(cat "$RUN/error" 2>/dev/null || echo "the build failed (exit code $code)")
    failures=$((failures + 1))
    local delay=$((60 * (1 << (failures > 9 ? 9 : failures - 1))))
    [ "$delay" -gt 21600 ] && delay=21600
    next_attempt=$(($(now) + delay))
    log "$last_error; trying again in $((delay / 60)) minutes."
    rm -rf "$DATA/next"
    rm -f "$RUN"/build.*
}

# ---- The server ----

stop_server() {
    if [ -n "$server_pid" ]; then
        kill "$server_pid" 2>/dev/null
        wait "$server_pid" 2>/dev/null
        server_pid=
    fi
    serving=false
}

# Starts the server when there's usable data and it isn't running, after a crash with a growing delay.
ensure_server() {
    if [ -n "$server_pid" ] && ! kill -0 "$server_pid" 2>/dev/null; then
        wait "$server_pid" 2>/dev/null
        server_pid=
        serving=false
        server_failures=$((server_failures + 1))
        next_server_start=$(($(now) + 5 * (1 << (server_failures > 6 ? 6 : server_failures))))
        log "the server stopped; starting it again at $(iso "$next_server_start")."
    fi

    if [ -z "$server_pid" ] && data_usable "$DATA/current" && [ "$(now)" -ge "$next_server_start" ]; then
        log "starting the server on data from $(meta data-date)."
        server_start
        server_pid=$!
        last_probe=0
    fi
}

# Whether the server answers, asked every 10 seconds, and while new data is loading.
probe_server() {
    if [ -z "$server_pid" ]; then
        serving=false
        return
    fi

    if [ "$serving" = true ] && [ $(($(now) - last_probe)) -lt 10 ]; then
        return
    fi

    last_probe=$(now)
    if server_ready; then
        if [ "$serving" = false ]; then
            log "the server answers, from data of $(meta data-date)."
        fi
        serving=true
        server_failures=0
    else
        serving=false
    fi
}

# After a swap: the new data is kept once the server answers from it, or undone when it doesn't within 10 minutes.
check_swap() {
    [ "$swap_deadline" -gt 0 ] || return 0
    if [ "$serving" = true ]; then
        swap_deadline=0
        rm -rf "$DATA/previous"
        failures=0
        last_error=
        next_attempt=0
        schedule_refresh_from_data
        log "serving the new data; the next refresh is due $(iso "$next_refresh")."
    elif [ "$(now)" -ge "$swap_deadline" ] || [ "$server_failures" -ge 3 ]; then
        swap_deadline=0
        last_error="the server doesn't start on the new data"
        log "$last_error: back to the previous data."
        stop_server
        rm -rf "$DATA/current"
        [ -d "$DATA/previous" ] && mv "$DATA/previous" "$DATA/current"
        failures=$((failures + 1))
        next_attempt=$(($(now) + 3600))
        server_failures=0
        next_server_start=0
    fi
}

# ---- What to do next ----

decide() {
    local t
    t=$(now)
    [ -n "$build_pid" ] && return
    [ "$swap_deadline" -gt 0 ] && return
    [ "$t" -lt "$next_attempt" ] && return

    if ! data_usable "$DATA/current"; then
        if [ -d "$DATA/current" ] && [ -f "$DATA/current/complete" ]; then
            last_error=${last_error:-"the data was built by $(meta version), and this image runs $VERSION"}
        fi
        start_build first
        return
    fi

    if [ -f "$DATA/refresh-now" ]; then
        start_build asked
        return
    fi

    if [ "$(meta rules)" != "$(data_rules)" ]; then
        log "the data was built with other rules than this image's: rebuilding it."
        start_build rules
        return
    fi

    if [ "$next_refresh" -gt 0 ] && [ "$t" -ge "$next_refresh" ]; then
        local id
        if ! id=$(source_id); then
            next_refresh=$((t + 3600))
            log "can't tell whether the source changed; asking again in an hour."
        elif [ "$id" = "$(meta source-id)" ]; then
            next_refresh=$((t + 86400))
            log "the source hasn't changed since the data was built; asking again tomorrow."
        else
            start_build refresh
        fi
    fi
}

# ---- Status ----

write_status() {
    local state stage= percent= t
    t=$(now)
    if [ -n "$build_pid" ]; then
        state=$([ "$serving" = true ] && echo refreshing || echo building)
        stage=$(cat "$RUN/build.stage" 2>/dev/null || true)
        percent=$(build_percent)
    elif [ "$swap_deadline" -gt 0 ] || { [ -n "$server_pid" ] && [ "$serving" = false ]; }; then
        state=starting
    elif [ "$t" -lt "$next_attempt" ]; then
        state=waiting
    elif [ "$serving" = true ]; then
        state=serving
    else
        state=building
    fi

    {
        printf '{"service":%s,"version":%s,"state":"%s","serving":%s,' \
            "$(json_string "$SERVICE")" "$(json_string "$VERSION")" "$state" "$serving"
        printf '"stage":%s,"percent":%s,"startedAt":%s,"dataDate":%s,' \
            "$(json_string "$stage")" "$(json_number "$percent")" "$(json_string "$([ -n "$build_pid" ] && iso "$build_started")")" \
            "$(json_string "$(meta data-date)")"
        printf '"lastError":%s,"nextAttemptAt":%s,"nextRefreshAt":%s,"heartbeat":"%s"}\n' \
            "$(json_string "$last_error")" "$(json_string "$([ "$t" -lt "$next_attempt" ] && iso "$next_attempt")")" \
            "$(json_string "$(iso "$next_refresh")")" "$(iso "$t")"
    } >"$RUN/status.tmp"
    mv "$RUN/status.tmp" "$RUN/status.json"
}

# The status server: socat, which hands each connection to status-http.sh.
ensure_status_server() {
    if [ -z "$httpd_pid" ] || ! kill -0 "$httpd_pid" 2>/dev/null; then
        RUN=$RUN socat -T 5 "TCP-LISTEN:$STATUS_PORT,reuseaddr,fork" EXEC:/usr/local/lib/essence/status-http.sh &
        httpd_pid=$!
    fi
}

shutdown() {
    log "stopping."
    [ -n "$build_pid" ] && kill -- "-$build_pid" 2>/dev/null
    stop_server
    [ -n "$httpd_pid" ] && kill "$httpd_pid" 2>/dev/null
    exit 0
}

supervise() {
    trap shutdown TERM INT
    VERSION=$(data_version)
    recover
    if [ -d "$DATA/current" ]; then
        log "starting $VERSION, with ${REFRESH_DAYS}-day refreshes, on data from $(meta data-date) built by $(meta version)."
    else
        log "starting $VERSION, with ${REFRESH_DAYS}-day refreshes; there's no data yet."
    fi
    write_status
    while true; do
        ensure_status_server
        if [ -n "$build_pid" ] && ! kill -0 "$build_pid" 2>/dev/null; then
            wait "$build_pid"
            finish_build $?
        fi

        ensure_server
        probe_server
        check_swap
        decide
        write_status
        sleep "$TICK" &
        wait $!
    done
}

# ---- Commands shared by both images ----

# The healthcheck: the supervisor's heartbeat, read through the status server, is under 2 minutes old.
health() {
    local status heartbeat
    status=$(curl -fsS --max-time 5 "http://127.0.0.1:$STATUS_PORT/status") || return 1
    heartbeat=$(printf '%s' "$status" | sed -n 's/.*"heartbeat":"\([^"]*\)".*/\1/p')
    [ -n "$heartbeat" ] && [ $(($(now) - $(date -d "$heartbeat" +%s))) -lt 120 ]
}

# Asks for a refresh now, without checking whether the source changed.
refresh() {
    touch "$DATA/refresh-now"
    echo "asked for a refresh; the status says when it starts (GET :$STATUS_PORT/status)."
}
