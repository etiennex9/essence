# Build context: the repository root (see docker-compose.yml).
#
# Routing for Best near you and Best on the way: OSRM's server and tools, with Essence's car profile, run by a supervisor
# (entrypoint.sh, src/Essence.Supervisor) that builds Québec's road data into the /data volume on first start and every
# month, serves it, and reports its progress at :9090/status. Bumping OSRM here makes the new image rebuild the data on
# its first start (data built by another version may not load); meanwhile the api estimates distances.
FROM ghcr.io/project-osrm/osrm-backend:v26.10.0-debian@sha256:55dbfd47d984c2cacd64e32901b4321c86b5ec761438e69aa053dc78698a786f

# curl downloads the extract and probes the server; osmium cuts the extract down to roads; socat serves the status; tini
# is PID 1 (signals, zombies). The OSRM image has none of them, nor the CA certificates.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl osmium-tool socat tini \
    && rm -rf /var/lib/apt/lists/*

COPY src/Essence.Routing/essence-car.lua /opt/essence-car.lua
COPY --chmod=755 src/Essence.Supervisor/supervisor.sh src/Essence.Supervisor/status-http.sh /usr/local/lib/essence/
COPY --chmod=755 src/Essence.Routing/entrypoint.sh /usr/local/bin/essence-routing

# The same user as the .NET images' app user. /data is created here so a new named volume mounted on it inherits the
# ownership.
RUN mkdir /data && chown 1654:1654 /data
USER 1654:1654
WORKDIR /data
EXPOSE 5000 9090
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --start-interval=5s --retries=3 CMD ["essence-routing", "health"]
ENTRYPOINT ["tini", "--", "essence-routing"]
CMD ["run"]
