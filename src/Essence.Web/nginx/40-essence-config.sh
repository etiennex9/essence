#!/bin/sh
# Writes the Blazor app's appsettings.json from the environment at container start, so one image works with any API address.
# nginx serves it from /tmp (see default.conf) because the image runs as a non-root user that can't write to the web root.
set -eu

# /api/ is this container's own proxy to the api service (see default.conf).
api_base_url="${ESSENCE_API_BASE_URL:-/api/}"
mkdir -p /tmp/essence
cat > /tmp/essence/appsettings.json <<EOF
{
  "Api": {
    "BaseUrl": "${api_base_url}"
  },
  "Map": {
    "StyleUrl": "${ESSENCE_MAP_STYLE_URL:-https://tiles.openfreemap.org/styles/positron}",
    "DarkStyleUrl": "${ESSENCE_MAP_DARK_STYLE_URL:-https://tiles.openfreemap.org/styles/dark}"
  }
}
EOF
echo "essence: the app calls the API at ${api_base_url}"
