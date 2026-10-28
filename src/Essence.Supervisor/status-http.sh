#!/usr/bin/env bash
# Answers one HTTP request on stdin (socat runs it for each connection): GET /status is the supervisor's status, as JSON.
status=${RUN:-/tmp/essence}/status.json

IFS=' ' read -r method target _ || exit 0
# The request's headers, up to the empty line, are of no use here.
while IFS= read -r -t 2 line && [ -n "${line%$'\r'}" ]; do :; done

respond() {
    printf 'HTTP/1.1 %s\r\nContent-Type: %s\r\nCache-Control: no-store\r\nContent-Length: %s\r\nConnection: close\r\n\r\n' "$1" "$2" "$3"
}

if [ "${target%%\?*}" = /status ] && { [ "$method" = GET ] || [ "$method" = HEAD ]; } && [ -f "$status" ]; then
    body=$(cat "$status")
    respond "200 OK" application/json "$(printf '%s\n' "$body" | wc -c)"
    [ "$method" = GET ] && printf '%s\n' "$body"
else
    respond "404 Not Found" text/plain 10
    [ "$method" = HEAD ] || printf 'Not found\n'
fi
