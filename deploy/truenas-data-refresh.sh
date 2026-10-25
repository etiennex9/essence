#!/bin/sh
# Rebuilds the data of one of the services whose data is built on the NAS, then restarts its container on it: routing
# (this month's roads) or geocoding (this week's addresses). Run from a monthly TrueNAS cron job per service, as root
# (README → Deploy → Routing, and Address search, have the setup):
#
#     sh /mnt/POOL/scripts/truenas-data-refresh.sh routing /mnt/POOL/apps/essence/data/routing
#     sh /mnt/POOL/scripts/truenas-data-refresh.sh geocoding /mnt/POOL/apps/essence/data/geocoding
#
# The build runs in a container of its own, from the image the service's container runs, so the data is built by the
# version that serves it (OSRM's, Photon's). The server keeps answering from the old data during the build (routing: 15–30
# minutes and a few GB of RAM on top of the server's; geocoding: 10–30 minutes and about 3 GB), and the restart takes it a
# minute at most. The data folder then holds current and previous. It prints a few lines when it's done, and the reason
# on stderr when it fails, so the cron job's emails say what happened.
#
# Also run it by hand after a rollout that bumped OSRM's or Photon's version (src/Essence.Routing/Dockerfile,
# src/Essence.Geocoding/Dockerfile): the new server refuses data built by the old one, so its container keeps restarting
# until this has run.
set -eu

usage="usage: truenas-data-refresh.sh <routing|geocoding> <data folder> [app name, default essence]"
service=${1:?$usage}
data=${2:?$usage}
app=${3:-essence}
case "$service" in
    routing | geocoding) ;;
    *)
        echo "$usage" >&2
        exit 2
        ;;
esac

# The image deploy/truenas.yaml runs, for when the app is stopped and has no container for the service (TrueNAS removes
# an app's containers when it stops or fails to start, after a version bump for one).
image_ref=ghcr.io/etiennex9/essence-$service:latest

if [ ! -d "$data" ]; then
    echo "$data isn't a folder: create the $service dataset first (README → Deploy)." >&2
    exit 1
fi

# One refresh at a time: a second would build into the same next folder.
exec 9>"$data/.refresh.lock"
if ! flock -n 9; then
    echo "a refresh is already running for $data." >&2
    exit 1
fi

# TrueNAS names an app's compose project ix-<app>. The build uses the image the service's container runs, so the data is
# built by the version that serves it.
container=$(docker ps --all --quiet --filter "label=com.docker.compose.project=ix-$app" --filter "label=com.docker.compose.service=$service")
if [ -n "$container" ]; then
    image=$(docker inspect --format '{{.Image}}' "$container")
elif docker image inspect "$image_ref" >/dev/null 2>&1; then
    image=$image_ref
    echo "$app: no $service container (the app is stopped, or the $service service isn't in its YAML): building with $image_ref."
else
    echo "no $service container in the $app app and no $image_ref image: add the $service service (README → Deploy)." >&2
    exit 1
fi
started=$(date +%s)
if ! docker run --rm --user 1654:1654 --volume "$data:/data" "$image" build >"$data/.refresh.log" 2>&1; then
    echo "the $service build failed; the server still runs on the previous data. Its log:" >&2
    tail -n 20 "$data/.refresh.log" >&2
    exit 1
fi

# A stopped app's container is left stopped: TrueNAS starts it with the app.
state=$([ -n "$container" ] && docker inspect --format '{{.State.Status}}' "$container" || echo none)
if [ "$state" != running ] && [ "$state" != restarting ]; then
    echo "$app: the $service data was rebuilt in $(( ($(date +%s) - started) / 60 )) minutes; the server uses it when the app starts."
    tail -n 1 "$data/.refresh.log"
    exit 0
fi

docker restart "$container" >/dev/null

# Healthy once it loaded the new data and answers; give it 5 minutes.
for _ in $(seq 60); do
    sleep 5
    status=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container")
    if [ "$status" = healthy ]; then
        echo "$app: the $service data was rebuilt in $(( ($(date +%s) - started) / 60 )) minutes and the server runs on it."
        tail -n 1 "$data/.refresh.log"
        exit 0
    fi
done

echo "$app: the $service data was rebuilt, but the server isn't healthy 5 minutes after its restart ($status). Its log:" >&2
docker logs --tail 20 "$container" >&2
exit 1
