#!/usr/bin/env bash
# Prints the images publish.yml should build, as its matrix: [{"app":"api","project":"Essence.Api"}, ...].
#
# An image is built when one of its inputs changed in the given commit range. Its inputs are read from its Dockerfile:
# the Dockerfile itself and what it copies from the repository (its project, the projects it references and the shared
# build files). Every image is built when there's no range, when the range can't be compared (rewritten history), or
# when .dockerignore or the publishing workflow changed. The web image is also built along with any other, and when a
# changelog entry or the release tool changed: it carries the release's version and changelog.
#
# Usage: select-images.sh [<range>], e.g. select-images.sh 06f1a06..HEAD
set -e
cd "$(git rev-parse --show-toplevel)"

range=$1
all=false
if [ -z "$range" ] || ! changed=$(git diff --name-only "$range" 2>/dev/null); then
    all=true
elif grep -qE '^(\.dockerignore|\.github/workflows/publish\.yml|\.github/scripts/select-images\.sh)$' <<<"$changed"; then
    all=true
fi
# .dockerignore keeps Markdown files out of the build context.
code_changes=$(grep -v '\.md$' <<<"$changed" || true)

selected=()
for dockerfile in src/*/Dockerfile; do
    project=$(basename "$(dirname "$dockerfile")")
    if [ "$all" = false ]; then
        # Path prefixes as a regex: the Dockerfile, then each COPY source taken from the build context (not --from=).
        inputs=$({ echo "$dockerfile"; awk '$1 == "COPY" && !/--from=/ { for (i = 2; i < NF; i++) if ($i !~ /^--/) print $i }' "$dockerfile"; } \
            | sed 's/[.]/\\./g' | paste -sd '|' -)
        grep -qE "^($inputs)" <<<"$code_changes" || continue
    fi
    selected+=("$project")
done

# The web image serves the release notes (tools/Essence.Release notes, from the day tags), so the version it shows is the
# whole release's: it's rebuilt with any other image, and when changelog/ or the tool changed.
if [ ${#selected[@]} -gt 0 ] || grep -qE '^(changelog/|tools/Essence\.Release/)' <<<"$code_changes"; then
    [[ " ${selected[*]} " == *" Essence.Web "* ]] || selected+=("Essence.Web")
fi

echo "Images to build: ${selected[*]:-none}${range:+ (changes in $range)}" >&2
matrix=""
for project in "${selected[@]}"; do
    app=${project#Essence.}
    matrix+="${matrix:+,}{\"app\":\"${app,,}\",\"project\":\"$project\"}"
done
echo "[$matrix]"
