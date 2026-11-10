#!/usr/bin/env bash
# Prints whether android.yml should build the Android app: true or false.
#
# The app is built when one of its inputs changed in the given commit range: its project, the projects it references
# (Essence.UI, and through it Essence.Contracts) and the shared build files. Markdown files don't count. It's built when
# there's no range, when the range can't be compared (rewritten history), or when the Android workflow or this script
# changed. A new project reference in src/Essence.Mobile needs adding here.
#
# Usage: select-android.sh [<range>], e.g. select-android.sh 06f1a06..HEAD
set -e
cd "$(git rev-parse --show-toplevel)"

range=$1
# --no-renames lists a moved file under its old path too, so that one moved out of the app's folders still counts.
if [ -z "$range" ] || ! changed=$(git diff --name-only --no-renames "$range" 2>/dev/null); then
    build=true
elif grep -qE '^\.github/(workflows/android\.yml|scripts/select-android\.sh)$' <<<"$changed"; then
    build=true
elif grep -v '\.md$' <<<"$changed" \
    | grep -qE '^(src/Essence\.(Mobile|UI|Contracts)/|Directory\.(Build|Packages)\.props$|global\.json$|\.editorconfig$)'; then
    build=true
else
    build=false
fi

echo "Android app: $([ "$build" = true ] && echo "build" || echo "unchanged")${range:+ (changes in $range)}" >&2
echo "$build"
