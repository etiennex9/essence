# Build context: the repository root (see docker-compose.yml).
#
# Address search for Best on the way: Photon's server (github.com/komoot/photon) on a Java runtime, run by a supervisor
# (entrypoint.sh, src/Essence.Supervisor) that builds the index of Québec's places into the /data volume on first start
# and every month, serves it, and reports its progress at :9090/status. Bumping Photon here makes the new image rebuild
# the index on its first start (an index built by another version may not load); meanwhile the search says it's coming.
FROM eclipse-temurin:21.0.12.1_1-jre-noble@sha256:35501d3f93173b420ac9001658eb68157e813316b865087e345c187fca422c4c

# curl downloads the dump and probes the server; zstd unpacks the dump and pv measures how far the import read it; socat
# serves the status; tini is PID 1 (signals, zombies).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl pv socat tini zstd \
    && rm -rf /var/lib/apt/lists/*

# The index records the version that built it (entrypoint.sh): keep PHOTON_VERSION and the jar together.
ENV PHOTON_VERSION=1.3.0
ADD --checksum=sha256:a89707c0045e4807b2a1180e132e68e108d998709f48b6c94b98a6e281f571a5 \
    https://github.com/komoot/photon/releases/download/1.3.0/photon-1.3.0.jar /opt/photon/photon.jar
COPY --chmod=755 src/Essence.Supervisor/supervisor.sh src/Essence.Supervisor/status-http.sh /usr/local/lib/essence/
COPY --chmod=755 src/Essence.Geocoding/entrypoint.sh /usr/local/bin/essence-geocoding

# The same user as the .NET images' app user. /data is created here so a new named volume mounted on it inherits the
# ownership.
RUN chmod 644 /opt/photon/photon.jar && mkdir /data && chown 1654:1654 /data
USER 1654:1654
WORKDIR /data
EXPOSE 2322 9090
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --start-interval=5s --retries=3 CMD ["essence-geocoding", "health"]
ENTRYPOINT ["tini", "--", "essence-geocoding"]
CMD ["run"]
