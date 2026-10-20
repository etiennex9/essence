#!/usr/bin/env python3
"""Redeploys a TrueNAS custom app when an image it runs from ghcr.io has a new digest under the same tag.

TrueNAS checks for image updates once a day, then waits for a click on Update. Run this instead from a TrueNAS cron job,
as root, every 5 minutes (README → Deploy has the setup):

    python3 /mnt/POOL/scripts/truenas-autodeploy.py essence --snapshot POOL/apps/essence/postgres

For each of the app's containers whose image comes from ghcr.io, it compares the digest the tag points to now with the
image the container runs. When one differs, it checks again every 2 minutes until two checks agree, so that it pulls a
whole release even while the publish workflow is still moving tags. Then it snapshots --snapshot's dataset (keeping its
last 10 snapshots), runs app.pull_images with redeploy (what Update does: pull every image, then stop and start the whole
app), waits for the app to report RUNNING (every health check passing), and checks that no container restarted in the 2
minutes after. It prints nothing while the app is up to date or ghcr.io or Docker can't be reached, a few lines when it
redeploys, and the reason on stderr when it fails, so the cron job's emails say what happened.

A set of digests is tried once: after a failure it waits for the next publish. If the pull failed, the app still runs
the old images; delete .autodeploy-<app>.json next to this script to retry them. If the redeploy or the app failed, the
app already has the new images: Stop and Start it in Apps once the cause is fixed, or publish a fix. A stopped app is
left alone. --check checks the setup, prints what it would redeploy, and changes nothing.

Written for TrueNAS 25.10 (app.pull_images, app.get_instance, and compose projects named ix-<app>).
"""
import argparse
import fcntl
import http.client
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

# An image index first: that's what Docker records as the repo digest when it pulls a multi-platform tag.
ACCEPT = ", ".join([
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])
KEEP_SNAPSHOTS = 10
# Essence's publish workflow moves latest one image at a time and retries a failed move for up to about 75 s, so two
# checks this far apart that agree mean it's done.
SETTLE_SECONDS = 120
SETTLE_LIMIT = 600  # at most this long, then the set seen last is rolled out
RUNNING_TIMEOUT = 600  # for the app to report RUNNING after the redeploy
STABLE_SECONDS = 120  # after RUNNING, for every container to stay up without a restart


class Unavailable(Exception):
    """ghcr.io or Docker can't be reached right now: the next run tries again."""


def run(*args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode:
        sys.exit(f"{' '.join(args)} failed:\n{(result.stderr or result.stdout).strip()}")
    return result.stdout


def remote_digest(ref):
    """The digest ghcr.io serves now for ref (ghcr.io/<owner>/<name>:<tag>). Anonymous: the package must be public."""
    repository, _, tag = ref.removeprefix("ghcr.io/").partition(":")
    try:
        with urllib.request.urlopen(f"https://ghcr.io/token?scope=repository:{repository}:pull", timeout=30) as response:
            token = json.load(response)["token"]
        request = urllib.request.Request(
            f"https://ghcr.io/v2/{repository}/manifests/{tag or 'latest'}", method="HEAD",
            headers={"Authorization": f"Bearer {token}", "Accept": ACCEPT})
        with urllib.request.urlopen(request, timeout=30) as response:
            digest = response.headers["Docker-Content-Digest"]
    except urllib.error.HTTPError as e:
        if e.code in (401, 403, 404):  # the package went private, or the package or tag is gone
            sys.exit(f"{ref}: ghcr.io answered {e.code} for {e.url}")
        raise Unavailable(f"ghcr.io answered {e.code} for {e.url}") from e
    except (OSError, ValueError, KeyError, TypeError, http.client.HTTPException) as e:
        raise Unavailable(f"can't reach ghcr.io: {e!r}") from e
    if not digest:
        raise Unavailable(f"ghcr.io sent no digest for {ref}")
    return digest


def docker(*args):
    """Like run(), but Docker being unreachable or a container going away meanwhile is Unavailable, not an error."""
    result = subprocess.run(["docker", *args], capture_output=True, text=True)
    if result.returncode:
        raise Unavailable(f"docker {args[0]}: {result.stderr.strip()}")
    return result.stdout


def containers(app):
    # The middleware runs each app as the compose project ix-<app>. A stopped app has no containers.
    return docker("ps", "--all", "--quiet", "--filter", f"label=com.docker.compose.project=ix-{app}").split()


def stale_images(app):
    """{ref: digest on ghcr.io} for each of the app's ghcr.io images whose containers run another digest."""
    ids = containers(app)
    stale = {}
    for line in (docker("inspect", "--format", "{{.Config.Image}} {{.Image}}", *ids).splitlines() if ids else []):
        ref, image_id = line.split()
        # Other registries (postgres from Docker Hub) and images pinned by digest are left to TrueNAS.
        if not ref.startswith("ghcr.io/") or "@" in ref or ref in stale:
            continue
        image = subprocess.run(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", image_id],
                               capture_output=True, text=True)
        if image.returncode and "No such image" not in image.stderr:
            sys.exit(f"docker image inspect {image_id} failed:\n{image.stderr.strip()}")
        # No such image: deleted with Force (Apps → Manage Images) from under a stopped container such as migrate.
        # Counting it as stale lets the redeploy pull it and recreate the container.
        running = (not image.returncode and json.loads(image.stdout)) or []
        digest = remote_digest(ref)
        if digest not in {repo_digest.partition("@")[2] for repo_digest in running}:
            stale[ref] = digest
    return stale


def settled_stale_images(app, tried):
    """stale_images(), once two checks SETTLE_SECONDS apart agree. A set that failed already isn't waited for."""
    stale = stale_images(app)
    deadline = time.monotonic() + SETTLE_LIMIT
    while stale and stale != tried and time.monotonic() < deadline:
        time.sleep(SETTLE_SECONDS)
        previous, stale = stale, stale_images(app)
        if stale == previous:
            break
    return stale


def snapshot(dataset):
    name = f"{dataset}@autodeploy-{datetime.now(timezone.utc):%Y%m%d-%H%M%S}"
    run("zfs", "snapshot", name)
    snapshots = run("zfs", "list", "-H", "-d", "1", "-t", "snapshot", "-o", "name", "-s", "creation", dataset).split()
    for old in [s for s in snapshots if "@autodeploy-" in s][:-KEEP_SNAPSHOTS]:
        subprocess.run(["zfs", "destroy", old])  # a held or cloned snapshot stays, and zfs says why on stderr
    return name


def wait_until_running(app):
    deadline = time.monotonic() + RUNNING_TIMEOUT
    while (state := json.loads(run("midclt", "call", "app.get_instance", app))["state"]) != "RUNNING":
        if time.monotonic() > deadline:
            sys.exit(f"{app}: still {state} {RUNNING_TIMEOUT // 60} minutes after the redeploy. "
                     f"See Apps → {app} (Workloads, logs) and /var/log/app_lifecycle.log.")
        time.sleep(15)
    # RUNNING counts a container waiting to restart as stopped, and one without a healthcheck as up once it starts, so a
    # crash-looping container can pass. The redeploy recreated every container: any restart since then is a crash.
    time.sleep(STABLE_SECONDS)
    ids = run("docker", "ps", "--all", "--quiet", "--filter", f"label=com.docker.compose.project=ix-{app}").split()
    crashed = []
    for line in (run("docker", "inspect", "--format", "{{.Name}} {{.State.Status}} {{.RestartCount}} {{.State.ExitCode}}",
                     *ids).splitlines() if ids else []):
        name, status, restarts, exit_code = line.split()
        if status == "restarting" or restarts != "0" or (status == "exited" and exit_code != "0"):
            crashed.append(f"{name.lstrip('/')} ({status}, {restarts} restarts, last exit code {exit_code})")
    if crashed:
        sys.exit(f"{app}: crashed after the redeploy: {', '.join(crashed)}. See Apps → {app} (Workloads, logs).")


def revision(ref):
    """The commit the image was built from, from the label docker/metadata-action sets."""
    labels = json.loads(run("docker", "image", "inspect", "--format", "{{json .Config.Labels}}", ref)) or {}
    return labels.get("org.opencontainers.image.revision", "")[:7] or "unknown commit"


def check(app, dataset, stale, tried):
    """--check: the app and the dataset exist, and what the next run would do."""
    print(f"{app}: app state {json.loads(run('midclt', 'call', 'app.get_instance', app))['state']}")
    if dataset:
        run("zfs", "list", "-H", "-o", "name", dataset)
        print(f"{app}: will snapshot {dataset} before each redeploy")
    if not stale:
        print(f"{app}: nothing to redeploy (up to date, or the app is stopped)")
    for ref, digest in stale.items():
        print(f"{app}: {ref} is now {digest}" + (" (tried already, waiting for the next publish)" if stale == tried else ""))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("app", help="the app's name in TrueNAS Apps, e.g. essence")
    parser.add_argument("--snapshot", metavar="DATASET", help="dataset to snapshot before each redeploy, e.g. POOL/apps/essence/postgres")
    parser.add_argument("--check", action="store_true", help="check the setup, print what would be redeployed, and change nothing")
    args = parser.parse_args()
    app = args.app
    state_file = Path(__file__).resolve().with_name(f".autodeploy-{app}.json")

    # One run per app at a time: a redeploy can outlast the cron interval.
    lock = open(f"/run/truenas-autodeploy-{app}.lock", "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        if args.check:
            print(f"{app}: another run is checking or redeploying it right now; try again in a few minutes")
        return

    try:
        tried = json.loads(state_file.read_text())
    except (OSError, ValueError):  # none yet, or left empty by a full pool
        tried = None
    try:
        stale = stale_images(app) if args.check else settled_stale_images(app, tried)
    except Unavailable as e:
        if args.check:
            sys.exit(f"{app}: {e}")
        return

    if args.check:
        check(app, args.snapshot, stale, tried)
        return
    if not stale or stale == tried:
        return

    print(f"{app}: new image for {', '.join(sorted(stale))}")
    if args.snapshot:
        print(f"{app}: snapshot {snapshot(args.snapshot)}")
    state_file.write_text(json.dumps(stale))
    run("midclt", "call", "-j", "-jp", "description", "app.pull_images", app, '{"redeploy": true}')
    wait_until_running(app)
    print(f"{app}: running " + ", ".join(f"{ref.rpartition('/')[2]} ({revision(ref)})" for ref in sorted(stale)))


if __name__ == "__main__":
    main()
