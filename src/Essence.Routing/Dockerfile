# Build context: the repository root (see docker-compose.yml).
#
# Routing for Best near you and Best on the way: OSRM's server and tools, with Essence's car profile. The map data isn't
# in the image: `build` makes it on the host, into the /data volume (entrypoint.sh). Bumping OSRM here is deliberate: the
# new image refuses data built by another version, so run the build right after the rollout (README → Deploy).
FROM ghcr.io/project-osrm/osrm-backend:v26.10.0-debian@sha256:55dbfd47d984c2cacd64e32901b4321c86b5ec761438e69aa053dc78698a786f

# curl downloads the data and checks the server's health; the OSRM image has neither it nor the CA certificates.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

COPY src/Essence.Routing/essence-car.lua /opt/essence-car.lua
COPY --chmod=755 src/Essence.Routing/entrypoint.sh /usr/local/bin/essence-routing

# The same user as the .NET images' app user. /data is created here so a new named volume mounted on it inherits the
# ownership.
RUN mkdir /data && chown 1654:1654 /data
USER 1654:1654
WORKDIR /data
EXPOSE 5000
ENTRYPOINT ["essence-routing"]
CMD ["serve"]
