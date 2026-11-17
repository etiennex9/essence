#!/usr/bin/env bash
# Prints whether android.yml should build the Android app: true or false.
#
# The app is built when one of its inputs changed in the given commit range: its project, the projects it references
# (Essence.UI, and through it Essence.Contracts and Essence.Hours) and the shared build files. Markdown files don't
# count. It's built when there's no range, when the range can't be compared (rewritten history), or when the Android
# workflow or this script changed. A new project reference in src/Essence.Mobile or Essence.UI needs adding here, and
# in android.yml's paths.
#
# With --labels, a change that came through a pull request labelled "web only" doesn't count: Essence.UI is the web app's
# too, and only a person can tell that a change to it matters only there (README → Android app releases). Each commit on
# main's first-parent line in the range is looked up on GitHub with gh: a merge commit is its pull request's, and its diff
# from its first parent is what the pull request brought. A commit counts when its pull request isn't labelled, when it
# has none, when GitHub can't say, and when it changes src/Essence.Mobile, which can't be web only. So does every commit
# when the range's start isn't on that line, and a change to the workflow or this script still builds, as above. Without
# --labels, nothing is looked up: the log lists what would be.
#
# Usage: select-android.sh [--labels] [<range>], e.g. select-android.sh --labels 06f1a06..HEAD
set -eo pipefail
cd "$(git rev-parse --show-toplevel)"

labels=false
if [ "${1-}" = --labels ]; then
    labels=true
    shift
fi
range=${1-}
label="web only"
inputs='^(src/Essence\.(Mobile|UI|Contracts|Hours)/|Directory\.(Build|Packages)\.props$|global\.json$|\.editorconfig$)'
# In a GitHub run, these lines show in its summary too.
notice=${GITHUB_ACTIONS:+::notice::}
warning=${GITHUB_ACTIONS:+::warning::}

# The app's inputs among a list of changed files, Markdown aside. Not grep -q: with pipefail, grep -v could be stopped by
# the pipe closing early, and the pipeline would read as no match.
app_inputs() { grep -v '\.md$' <<<"$1" | grep -E "$inputs" || true; }

build=""
# --no-renames lists a moved file under its old path too, so that one moved out of the app's folders still counts.
if [ -z "$range" ] || ! changed=$(git diff --name-only --no-renames "$range" 2>/dev/null); then
    build=true
elif grep -qE '^\.github/(workflows/android\.yml|scripts/select-android\.sh)$' <<<"$changed"; then
    build=true
else
    app=$(app_inputs "$changed")
    [ -n "$app" ] || build=false
fi

# The app's inputs changed: which commits changed them, and through which pull requests. Each commit's diff from its first
# parent adds up to the range's only when the range's start is the first parent of its oldest commit.
counted=0
skipped=()
if [ -z "$build" ]; then
    start=${range%%..*}
    end=${range#*..}
    commits=""
    if [[ $range == *..* && $range != *...* && -n $start && -n $end ]]; then
        commits=$(git rev-list --first-parent --reverse "$start..$end")
    fi
    oldest=$(head -n 1 <<<"$commits")
    parent=$( [ -z "$oldest" ] || git rev-parse --verify --quiet "$oldest^1" || true)
    if [ -z "$parent" ] || [ "$parent" != "$(git rev-parse --verify --quiet "$start^{commit}" || true)" ]; then
        echo "Android app: $range doesn't start on main's first-parent line, so every change counts" >&2
        build=true
    fi
fi

if [ -z "$build" ]; then
    while read -r commit; do
        files=$(git diff --name-only --no-renames "$commit^1" "$commit")
        files=$(app_inputs "$files")
        [ -n "$files" ] || continue

        if [ "$labels" = false ]; then
            echo "${commit::7} changes the app: with --labels, its pull request's labels would be looked up" >&2
            counted=$((counted + 1))
            continue
        fi

        # A merge commit names its pull request; otherwise (a squash merge) GitHub knows which one the commit came from.
        # Either way, only the pull request whose merge made this very commit.
        subject=$(git log -1 --format=%s "$commit")
        if [[ $subject =~ ^Merge\ pull\ request\ \#([0-9]+)\  ]]; then
            endpoint="repos/{owner}/{repo}/pulls/${BASH_REMATCH[1]}"
            each='.'
        else
            endpoint="repos/{owner}/{repo}/commits/$commit/pulls"
            each='.[]'
        fi
        query="[$each | select(.merged_at != null and .merge_commit_sha == \"$commit\")] | first
            | if . == null then \"\" else \"\(.number) \(any(.labels[]; .name | ascii_downcase == \"$label\"))\" end"
        # Not from the loop's input, which lists the commits.
        if ! pr=$(gh api "$endpoint" --jq "$query" </dev/null); then
            echo "${warning}Couldn't ask GitHub which pull request brought ${commit::7}, so it counts." >&2
            counted=$((counted + 1))
            continue
        fi

        number=${pr%% *}
        if [ -z "$pr" ]; then
            echo "${commit::7} changes the app, through no pull request: it counts" >&2
            counted=$((counted + 1))
        elif [ "${pr#* }" != true ]; then
            echo "${commit::7} changes the app, through #$number: it counts" >&2
            counted=$((counted + 1))
        elif grep -q '^src/Essence\.Mobile/' <<<"$files"; then
            echo "${warning}#$number is labelled $label but changes src/Essence.Mobile, so it counts." >&2
            counted=$((counted + 1))
        else
            echo "${commit::7} changes the app, through #$number, labelled $label: skipped" >&2
            skipped+=("#$number")
        fi
    done <<<"$commits"

    # Nothing counted and nothing skipped can't happen when the diffs add up: build rather than trust that.
    if [ "$counted" -gt 0 ] || [ ${#skipped[@]} -eq 0 ]; then
        build=true
    else
        build=false
        echo "${notice}The Android app isn't built: its inputs changed only through pull requests labelled $label (${skipped[*]})." >&2
    fi
fi

echo "Android app: $([ "$build" = true ] && echo "build" || echo "unchanged")${range:+ (changes in $range)}" >&2
echo "$build"
