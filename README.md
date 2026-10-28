# Essence

Continuously ingests Québec fuel prices published by the Régie de l'énergie
(`https://regieessencequebec.ca/stations.geojson.gz`, about 2,460 stations, regenerated every 5 minutes)
into PostgreSQL, keeping the station list and a full price-change history, serves them through a read-only HTTP API,
and shows them on a map of nearby stations.

| Project | What it does |
|---|---|
| `src/Essence.Data` | EF Core model, migrations, `StationKey` normalizer. Shared by every app. |
| `src/Essence.Ingest` | Worker: polls the feed, archives each new file, diffs it against the database and writes only the changes. Backfills missed versions from the Régie's xlsx files. |
| `src/Essence.Api` | ASP.NET Core minimal API (read-only DB role), OpenAPI + Scalar UI. |
| `src/Essence.Migrator` | One-shot: applies migrations, creates the `essence_writer` / `essence_reader` roles and grants them. |
| `src/Essence.Contracts` | The API's response records and the release notes', shared by the API, the UI and the release tool. |
| `src/Essence.Ranking` | Which station to fill up at: costs, savings, the pick and its rules, the search area; for trips, the route's corridor, km along it, detours and the range; the OSRM and Photon clients, the address search's labels, and Québec's boundary. No dependencies; used by the API. |
| `src/Essence.Routing` | The routing image: OSRM with Essence's car profile, which builds and refreshes Québec's road data itself (see [Routing and address search](#routing-and-address-search)). |
| `src/Essence.Geocoding` | The address search image: Photon, which builds and refreshes its index of Québec's places itself. |
| `src/Essence.Supervisor` | The supervisor both of those images run: it builds their data, serves it, refreshes it, retries, and reports its progress at `:9090/status`. |
| `src/Essence.UI` | Razor class library: the map page, the station details pane and everything behind them. Shared with a future MAUI app. |
| `src/Essence.Web` | Blazor WebAssembly host for `Essence.UI`, served as static files by nginx. See [its README](src/Essence.Web/README.md). |
| `tools/Essence.Release` | Run by the publish workflow: dates each release by the day it landed on `main` and writes the web app's changelog (see [Versions](#versions)). |
| `tests/Essence.Ingest.Tests` | xUnit unit tests: parsing (JSON and xlsx), diffing, scheduling, backfill planning, fetch decisions, archive. |
| `tests/Essence.Ranking.Tests` | xUnit unit tests: fill costs, the Best near you ranking and its rules, the search area, eligibility; polylines, the corridor and km along, the trip's candidates, detours, range and ranking; the OSRM and Photon clients against stub servers; the address search's labels and filtering; Québec's boundary at its borders. |
| `tests/Essence.Api.Tests` | xUnit tests of the API in memory (WebApplicationFactory, no database): the address search's endpoints against a stub Photon, their rules, rate limit and logs. |
| `tests/Essence.UI.Tests` | xUnit unit tests: marker tones and loading, price history series, brands, formatting, the API client, car profiles, Best near you's refreshes, the open trip and saved places, the address search's debounce and cache, pins' addresses, the route's split at the range, Google Maps links, the release notes, translations. |
| `tests/Essence.Release.Tests` | xUnit unit tests: release days, changelog dating and pages, and every entry in `changelog/`. |

The full design and its rationale are in [docs/PLAN.md](docs/PLAN.md).

## Run it

Requires Docker Desktop.

```bash
cp .env.example .env    # then edit the passwords
docker compose up --build
```

Compose starts `postgres`, then `migrate` (exits 0 once the schema and roles are in place), then `ingest` and `api`, and `web`.
The map is at <http://localhost:8081>. The API listens on <http://localhost:8080>; the interactive docs are at <http://localhost:8080/scalar>.
The web container also forwards `/api/` to the API (<http://localhost:8081/api/health>): that's the address the map uses.

Road distances, Best on the way and the address search need the routing and geocoding services, in the `trip` profile:

```bash
docker compose --profile trip up -d
```

On their first start they build their data into the `routing` and `geocoding` volumes: Québec's roads (a 1.2 GB download
from Geofabrik, then a couple of minutes and 3.2 GB of RAM) and the index of Québec's places (a 1.6 GB download of
Photon's dump of Canada, then about 10 minutes and 3 GB of RAM). Meanwhile Best near you estimates distances, and the
map says trips and the search will be ready soon, with how far they are; <http://localhost:8080/services> says it too.
They refresh their data every `REFRESH_DAYS` days (30). `ROUTING_PBF_URL` and `GEOCODING_DUMP_URL` in `.env` point them
at other copies of the downloads.

```bash
curl localhost:8080/health
curl "localhost:8080/stations/nearby?lat=45.5017&lon=-73.5673&radiusKm=3&fuel=Diesel"
curl "localhost:8080/stations/best?lat=45.5017&lon=-73.5673&fuel=Regulier"
curl "localhost:8080/trips/best?fromLat=45.5017&fromLon=-73.5673&toLat=46.8139&toLon=-71.208&fuel=Regulier&rangeKm=180"
curl "localhost:8080/places/search?q=1000%20de%20la%20gauch&lat=45.5017&lon=-73.5673&lang=fr"
curl "localhost:8080/places/reverse?lat=45.4988&lon=-73.5666"
curl localhost:8080/services
curl "localhost:8080/prices/current?fuel=Régulier&limit=5"
curl localhost:8080/imports
```

Look at the data with `docker compose exec postgres psql -U essence_admin essence`, and at the raw archive with
`docker compose exec ingest ls -R /data/archive`.

## Develop

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

`dotnet test` runs on Microsoft.Testing.Platform (opted in via `global.json`).
To add a migration after changing the model:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Essence.Data --startup-project src/Essence.Data
```

To work on the map, run the back end in Docker and the web app with hot reload; it opens on <http://localhost:5173>,
an origin the API already allows:

```bash
docker compose up -d postgres migrate ingest api
```

```bash
dotnet watch --project src/Essence.Web
```

To run an app outside Docker, give it a connection string, e.g.
`ConnectionStrings__Essence="Host=localhost;Database=essence;Username=essence_writer;Password=…"`.
In the Development environment, logs are plain text and the ingest archive goes to `./data/archive`.

## How ingestion works

- **Schedule.** Files are generated at :00:01, :05:01, … UTC and reach the CDN 40–90 s later. The worker expects the next file
  at the last `generated_at` + 5 min and opens a polling window after a learned delay (20th percentile of the last 50 delays,
  clamped to 20–120 s, 40 s at first). In the window it polls every 10 s; after 3 min without a new file it polls every 60 s until
  the next window. After an import it sleeps until the next window. Every poll time gets ±2 s of jitter.
- **Fetch.** Conditional GET with the ETag of the newest imported file. A response is discarded if its `generated_at` is not newer
  than the last import (the CDN flip-flops between edges) or if its SHA-256 matches the last import.
- **Backfill.** Each version is also published as `https://regieessencequebec.ca/data/stations-{generated_at:yyyyMMddHHmmss}.xlsx`
  (the feed's `excel_url`), and those files stay online for about an hour. When a new version arrives more than one slot after
  the last import (after a restart, an outage or a hung request), the worker first imports the missed slots from their xlsx,
  oldest first, then the new version. There is no directory listing, so it guesses each name: the slot time with seconds `01`,
  then `00`, then `02`. Slots older than `Backfill:MaxAge` (70 min) aren't requested; the log says how many were lost.
  Backfilled imports have `source = 'xlsx'` and no delay, so they don't affect the learned delay.
- **Archive.** Each new file is saved as received (gzipped JSON, or xlsx) to
  `/data/archive/yyyy/MM/dd/stations-{generated_at}.geojson.gz` (or `.xlsx`) before it is imported. Files older than 30 days are deleted once a day.
- **Import.** One transaction, guarded by a PostgreSQL advisory lock: the current stations and prices are loaded, diffed in memory,
  and only the changes are written. The effective time of every change is the file's `generated_at`.
  A failed import is rolled back, recorded as a `failed` row in `imports` and retried on the next poll; after 3 failures the file
  is skipped (it stays in the archive).

### Data rules

- Stations have no ID in the feed. Their identity is `natural_key`: the address (trimmed, whitespace collapsed, lowercase,
  accents removed) and the postal code (uppercase, no spaces). A changed address therefore shows up as a new station.
- Edited station fields overwrite the row and are logged in `station_changes`. A station missing from the file is deactivated
  and its prices closed; if it comes back it is reactivated (logged as `is_active`).
- Prices are cents per litre (`numeric(5,1)`), null when unavailable. `price_history` gets a row only when a price or its
  availability changes; `current_prices` mirrors the open rows.
- Coordinates of 0,0 become null. An empty or "Aucun" brand becomes null. An unparsable price skips that one price (its current
  value is kept) and counts as a warning in the import.
- The xlsx has the same data in another shape, with two gaps. It has no status column, so a backfill keeps the stored status
  (a brand-new station gets "En opération", the only status ever seen). And "N/D" means either "unavailable" or "not sold":
  it marks the fuel unavailable if the station currently has a price for it, and is ignored otherwise. With these rules the xlsx
  and the JSON of the same version produce no changes against each other.

## API

| Endpoint | |
|---|---|
| `GET /stations?region&brand&active&q&page&pageSize` | Stations, paged. `q` searches the name, address and postal code. |
| `GET /stations/{id}` | One station with its current prices. |
| `GET /stations/nearby?lat&lon&radiusKm=5&fuel&limit=20` | Active stations within a radius, closest first. |
| `GET /stations/best?lat&lon&fuel&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&radiusKm=10` | Where to fill up near a point: the pick, the closest, the cheapest and a few others that save (see below). |
| `GET /trips/best?fromLat&fromLon&toLat&toLon&fuel&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&rangeKm&spareKm=30` | Where to fill up on the way to a destination: the route, where to stop, and the stations along it ranked by detour (see below). |
| `GET /places/search?q&lat&lon&lang=fr` | Places in Québec matching what was typed, at most 5, those near `lat`,`lon` first (see below). |
| `GET /places/reverse?lat&lon&lang=fr` | What a point in Québec is called: the nearest address, street or named place. |
| `GET /services` | Whether trips and the address search are ready, or how far their data is (`state`, `stage`, `percent`). |
| `GET /stations/{id}/prices?fuel&from&to` | Price history overlapping a range (default: the last 30 days). |
| `GET /prices/current?fuel&region&brand&sort=price&includeUnavailable&limit=50` | Current prices, cheapest first. |
| `GET /imports?limit=20` | Recent imports with their statistics and `source` (`feed` or `xlsx`). |
| `GET /health` | 200 when the database answers and the last successful import is under 15 min old, else 503. A routing or geocoding server that doesn't answer, or whose data is being built, makes it `Degraded`, still 200, with the build's stage and percent. |

`/stations/best` ranks the operating stations with an available price for the fuel (Costco only with `costco=true`: it
sells to members only) by what the fill-up costs once the drive there and back is counted: price ÷ 100 × (fill + km there
and back × L/100 km ÷ 100), to the cent. Savings are counted against the closest station. A farther one is picked only if
it saves at least $0.25 and its round trip takes at most `maxDetourMin` longer than the closest's; the others stay
listed, with the reason. Distances and minutes are by road, from the routing service: the 10 nearest stations and every
one cheaper than one of them, at most 200. When the routing service is down, or the point is more than 1 km from a road,
they're estimated instead (`byRoad` false: straight line × 1.3, driven at 40 km/h), and a farther station must also save
with roads twice the straight line. It looks within `radiusKm`, widened to 25 km with fewer than 5 stations, else returns
the closest priced station at any distance. It ranks a copy of every active station and its current prices, kept in
memory and loaded once per import. The rules are in `src/Essence.Ranking`.

`/trips/best` routes from the start to the destination, then looks at the stations within `maxDetourMin` × 0.5 km of the
route (between 1 and 8 km), at most 100: in each 10 km stretch the two closest to the route and the three cheapest (on a
long route, as many of them as 100 allow, the closest and the cheapest of each stretch first), then the cheapest of the
rest. Each is measured by road, start → station → destination, and its detour (what stopping adds
to the trip) counts in its cost. The reference is the cheapest station on the way (a stop that adds at most 1 km and
2 min), or with none, the smallest detour; the pick is the lowest cost within `maxDetourMin`, if it saves at least
$0.25; the biggest saving is shown too when it's another station. On trips of 100 km or more, `rangeKm` (what the
dashboard says is left) less `spareKm` says which stations the car reaches and where to stop (`stopBeforeKm`); when that
reaches no station, `lowFuel` is set and the whole range counts, or else the nearest station. Québec only: a destination
outside Québec's boundary (OpenStreetMap's, simplified to 20 m along the inhabited borders; `QuebecBoundary.txt`), a start
outside its box, or either more than 1 km from a road gets a 422 that says which (`point`: `from` or `to`), and so does a
trip with no route; 503 when the routing service is down. The start is only checked against the box: a driver just
across the border still gets the stations on the way into Québec. Stations snap to their own road: the car profile
leaves autoroutes out, so a station on a service road beside one gets its real detour.

`/places/search` finds addresses, streets, towns and named places in an index of Québec's places from OpenStreetMap
(Photon, in the geocoding container), within Québec's box and near `lat`,`lon` when they're given; places whose province
isn't Québec are dropped, each place comes once (a long street's stretches in one town are one), and a business found
only by its address comes after the address. `q` takes 3 to 100 characters; `lang` is `fr` or `en`. `/places/reverse`
answers 422 for a point outside Québec's boundary (known without Photon), 404 when nothing with an address is within
1 km, else the nearest address within 100 m, or the nearest street or named place. Both answer 503 when Photon is down;
they wait 1.5 s for it, without retrying.

While the routing data or the address index is built (or loaded, after a refresh), the API reads the containers'
progress (`:9090/status`, every 5 seconds at most) instead of calling them: `/stations/best` estimates, and
`/trips/best` and `/places/*` answer 503 with `service`, `state`, `stage` and `percent` in the problem, as `/services`
says them. A container whose status can't be had is called as before.

`/stations/{id}`, `/stations/nearby`, `/stations/best` and `/trips/best` send `Last-Modified`: when the latest import was fetched. The map refreshes its prices
about 5 min 20 s after that time, which is when the next import should be in, and more often while that import is late. A request
whose `If-Modified-Since` is still current gets a 304.

Fuel names accept or omit accents (`Regulier` = `Régulier`). Errors are ProblemDetails. Requests are rate limited per client IP
(`RateLimiting:PermitLimit` per `RateLimiting:WindowSeconds`, 120/60 s by default); `/places/` has a window of its own
(`RateLimiting:Search:PermitLimit` per `RateLimiting:Search:WindowSeconds`, 60/60 s), so typing doesn't eat the rest. The `X-Forwarded-For`, `-Proto` and `-Prefix`
headers are trusted from private networks only, where the web container that forwards `/api/` lives; that container in turn
takes the client's address from a reverse proxy on a private network. Allowed CORS origins come from `Cors:AllowedOrigins`:
in Compose, `CORS_ORIGIN` from `.env`. The web container needs none, since the map calls the API through its own `/api/`.

## Deploy

On every push to `main` that changes more than Markdown files and `docs/`,
[the publish workflow](.github/workflows/publish.yml) runs the tests, then builds the images whose inputs changed since its
last successful push run and pushes them to the GitHub Container Registry:
`ghcr.io/etiennex9/essence-{migrator,ingest,api,web,routing,geocoding}`, tagged `sha-<commit>`. Once all of them are pushed, `latest` moves to
them together, so a new migrator doesn't run next to an old api. If an image fails to build, `latest` stays. If the move
itself fails partway, use **Re-run failed jobs** on that run, but only while it's the latest run on `main`: a later run
builds its images again and moves `latest` past them, and re-running the older one after that would move `latest` back.
On `main`, a run waits for the one in progress instead of canceling it; a push made while a manual run waits replaces the
manual run, so start that again afterwards. An image's inputs are read from
its Dockerfile: the projects it copies (its own and those it references) and the shared build files such as
`Directory.Packages.props` ([select-images.sh](.github/scripts/select-images.sh)). A manual run, or a change to the workflow
or `.dockerignore`, builds all six. The web image is also built along with any other, and when a changelog entry
changed: it carries the release's version and changelog ([Versions](#versions)). Pull requests build the images they
affect, without pushing.

[deploy/truenas.yaml](deploy/truenas.yaml) runs those images on TrueNAS (Apps → Discover Apps → ⋮ → Install via YAML).
Only the web container publishes a port; a reverse proxy in front of it serves the whole app, API included, at one HTTPS address.
Leave asset caching off on that proxy: the web app's files under `_content/` aren't fingerprinted, and a new release reaches
open tabs only if they're revalidated ([Versions](#versions)). The web container doesn't log `/api/stations/best`,
`/api/stations/nearby`, `/api/trips/` and `/api/places/`, whose query strings say where users are, where they're going
and what they typed, and logs only critical errors under `/api/` (an upstream error names the request); the routing and
geocoding servers log no requests, and the api logs their failures without the request. A proxy in front logs
query strings too unless told not to.

### Rolling out each push automatically

TrueNAS checks for new images once a day and waits for a click on **Update**. Instead, a cron job on the NAS can run
[truenas-autodeploy.py](deploy/truenas-autodeploy.py) every 5 minutes: when a `latest` tag on GHCR points to a newer image
than the app runs, it waits until the whole release is published, snapshots the database's dataset, then pulls and
redeploys the app (what **Update** does), waits for it to report running, and checks that no container keeps restarting.
Nothing reaches the NAS from outside and no API key is involved. Written for TrueNAS 25.10, for
the app installed as `essence` from [deploy/truenas.yaml](deploy/truenas.yaml); replace `POOL` with your pool's name.
Start once the Publish run for the commit that added the script has succeeded, promote job included (**Actions →
Publish**): its api is the first with `/alive`, which step 4 needs.

1. **Datasets → Add Dataset** on your pool: name `scripts`, preset **Generic**. Don't share it: root runs what's in it.
2. In **System → Shell**, run `sudo -i` unless the prompt already ends in `#` (otherwise it ends in `$` or `%`), entering
   your password if asked, then:
   ```sh
   curl -fsSL -o /mnt/POOL/scripts/truenas-autodeploy.py https://raw.githubusercontent.com/etiennex9/essence/main/deploy/truenas-autodeploy.py
   chown -R root:root /mnt/POOL/scripts && chmod 700 /mnt/POOL/scripts
   python3 /mnt/POOL/scripts/truenas-autodeploy.py essence --snapshot POOL/apps/essence/postgres --check
   ```
   `--check` changes nothing. It prints the app's state, that it will snapshot the dataset, and either
   `nothing to redeploy` or the images it would roll out; an error means a wrong app or dataset name.
3. Roll out the current images once by hand, which also tells you the script works end to end:
   ```sh
   python3 /mnt/POOL/scripts/truenas-autodeploy.py essence --snapshot POOL/apps/essence/postgres
   ```
   It prints what it does, and nothing if the app already runs the latest images. A rollout takes a few minutes: it waits
   2 minutes for the release to settle and 2 more after the app runs, to catch a container that keeps restarting.
4. If the app was installed from a `deploy/truenas.yaml` whose `web` service has no `healthcheck`, add that block now
   (**Apps → essence → Edit**, keeping your passwords and paths, then **Save**), so that the app reports running only once
   the api answers. Only once the app runs an api with `/alive` (step 3 rolled out `essence-api`, or printed nothing
   after that Publish run): an older api would keep `web` unhealthy, and the app on **Deploying**.
5. So that failures reach you: in **Credentials → Users**, set the **Email** of `root`, which the cron job mails. Then
   **System → General Settings → Email → Settings**: set up a mail server, **Send Test Mail** (it goes to `root` and any
   other local administrator with an email), then **Save**. The test uses the form as filled in; cron mails use only saved
   settings.
6. **System → Advanced Settings → Cron Jobs → Add**:
   - **Description**: `Autodeploy essence`
   - **Command**: `python3 /mnt/POOL/scripts/truenas-autodeploy.py essence --snapshot POOL/apps/essence/postgres`
   - **Run As User**: `root`
   - **Schedule**: **Custom**; in the dialog, **Minutes** `*/5`, **Hours** and **Days of Month** `*`, every day of the
     week and month checked, **Done**
   - **Hide Standard Output**: checked to be mailed only failures, cleared to also get a mail for each rollout
   - **Hide Standard Error**: cleared
   - **Enabled**: checked

From then on, each push that publishes an image is running within about 10 minutes of the publish run's end (the next
5-minute run, 2 minutes for the release to settle, then the pull, restart and health checks), and the database's dataset
gets an `@autodeploy-<time>` snapshot first (the last 10 are kept).

- **A failed rollout** is mailed and not retried: the next publish is. If the pull failed, the app still runs the old
  images; to retry, `rm /mnt/POOL/scripts/.autodeploy-essence.json`. If migrate failed, or the app wasn't running 10
  minutes later, or a container restarted in the 2 minutes after, the app already has the new images: fix the cause and
  **Stop**, then **Start** the app in **Apps**, or push a fix.
- **To hold an image back**, set it to `:sha-<commit>` in the app's YAML (**Apps → essence → Edit**). A sha tag doesn't
  move, so the script leaves it alone until you set it back to `:latest`. Only the images a push rebuilt get its sha tag:
  pick one listed on the image's package page (the repository's **Packages**). Holding `web` back also keeps the version
  and changelog it shows. To pause everything, disable the cron job.
- **Migrations only go forward**, so rolling back the images doesn't undo one. Stop the app. Roll the postgres dataset back
  to the `@autodeploy-` snapshot taken before the bad rollout: **Datasets** → the dataset → **Data Protection** → **View
  Snapshots** → that snapshot's row → **Rollback**. If newer snapshots exist, the default choice is refused: choose
  **Newer Clone**, which deletes the snapshots taken after it. Then, in **Apps → essence → Edit**, set the images to the
  `sha-<commit>` tags of the release before, **Save**, and **Start** the app: Save doesn't start a stopped app, and the
  cron job leaves a stopped app alone. Starting it on `latest` instead would run the new migration again.
- **To update the script**, look at what changed in `deploy/truenas-autodeploy.py`, since root runs it, then run the
  `curl` command again.

### Routing and address search

Road distances, Best on the way and the address search come from the `routing` container (OSRM, Québec's roads from
Geofabrik, [src/Essence.Routing](src/Essence.Routing)) and the `geocoding` container (Photon, Québec's places from Photon's
dump of Canada, [src/Essence.Geocoding](src/Essence.Geocoding)). Both are in [deploy/truenas.yaml](deploy/truenas.yaml):
pasting it is all they need. Each runs a supervisor ([src/Essence.Supervisor](src/Essence.Supervisor)) that:

- builds its data on its first start, into its volume, and serves it once it's built. The app reports running at once:
  the healthcheck only asks whether the supervisor is alive, so a first build doesn't hold the autodeploy's 10-minute
  wait. Meanwhile Best near you estimates distances, and the map says trips and the search will be ready soon, with how
  far they are (*Trips will be ready soon: preparing Québec's road map (45 %)*), and switches them on when they are.
- refreshes it every `REFRESH_DAYS` days (30; 0 never), when the source changed (Geofabrik's extract daily, the dump
  weekly): it builds the new data beside the current one while the server keeps answering, then restarts the server on
  it, which takes the server off for a few seconds, and keeps the previous data until the new one loads.
- rebuilds the data after an image bumps OSRM's or Photon's version (data built by another version may not load), or
  changes the car profile.
- retries a failed download or build after 1 minute, then twice as long each time, up to 6 hours; a restart throws a
  build in progress away, and a download resumes where it stopped.
- reports what it's doing at `:9090/status`, which the api reads (`/health`, `/services`).

**Storage: Docker volumes, not datasets.** The data is a cache that rebuilds itself, so it lives in the named volumes
`routing` and `geocoding` that the YAML declares: Docker creates them in the apps pool (`ix-apps`), owned by the
containers' user, with nothing to create or `chown` by hand. TrueNAS deletes them with the app, and leaves them out of
its app backups; either way the containers rebuild them. The routing volume holds about 0.9 GB, 3 GB while it builds;
the geocoding volume about 2 GB, 6 GB while it builds.

**Memory:** `mem_limit: 5g` each, measured while building and serving at once: osrm-extract takes 3.2 GB (the extract is
cut down to roads first with osmium, which gives the same routes; the whole extract took 6.6 GB) and the server 0.7 GB;
Photon's import about 2.7 GB with its server. Serving alone takes about 0.7 GB and 0.4–1.3 GB.

**By hand, if ever:** `docker exec ix-essence-routing-1 essence-routing refresh` (or `ix-essence-geocoding-1
essence-geocoding refresh`) rebuilds now without waiting for the source to change; `docker exec ix-essence-routing-1
curl -s localhost:9090/status` shows the status. Their logs say what they did.

**The packages.** The publish workflow creates `essence-routing` and `essence-geocoding` on GHCR the first time it
builds them. A package a workflow creates in a public repository should be public too; if one isn't, the Publish run's
summary warns about it, and its owner makes it public once (the repository's **Packages** → the package → **Package
settings** → **Change visibility**): the autodeploy reads digests anonymously.

## Versions

The app's version is a date: the day the newest change it was built from landed on `main`, counted in Québec time
(America/Toronto), e.g. `2026-10-19`. Everything that lands on one day is one version. The button in the map's
bottom-left corner shows the version and, under *What's new*, each version's changes, newest first; older months load as
the list scrolls.

**Adding an entry.** A change that users would notice adds a file to [changelog/](changelog/), named freely, with an
English and a French sentence:

```json
{
  "en": "On phones, drag a station’s panel up to see more of it, or down to close it.",
  "fr": "Sur téléphone, faites glisser le panneau d’une station vers le haut pour l’agrandir, ou vers le bas pour le fermer."
}
```

No date: the entry gets the day it lands on `main`, however long its pull request stayed open. Only an entry written
after the fact has `"date": "YYYY-MM-DD"`. Renaming a file dates it again, to the day of the rename, so give a renamed
entry a `date` too. A test checks that every file has both languages and nothing else. Changes users wouldn't notice (CI,
refactors) add no file: their day is still a version, with nothing listed under it.

**How a day is known.** Git doesn't record when a commit reached `main`: a commit's date is when it was written, and a run
can start after midnight for a push made before. GitHub does: its
[activity log](https://docs.github.com/en/rest/repos/repos#list-repository-activities) lists each push, merge and force
push to `main` with its time. On each run on `main`, the publish workflow's `tag` job (`tools/Essence.Release tag`) reads it
and points the tag `vYYYY-MM-DD` at what `main` was at the end of that day. It derives every day of the log's last 100
moves again, so a run that was replaced while it waited is made up for by the next one. The tags are the lasting record
(GitHub doesn't say how long it keeps the log). They belong to the workflow, which moves them: don't create or move them
by hand, and a tag ruleset on `v*` would have to let GitHub Actions bypass it. Only the tag of the commit being built has
to be set for its images to be released; another day's that can't be set is a warning in the job's log, and until a
later run sets it, that day's entries are dated by the next tag.

**The deploy key.** The job pushes the tags with an SSH deploy key, because GitHub refuses the workflow's own token a tag
at some commits that changed a workflow file (`Resource not accessible by integration`). Without the key, or if a push
with it fails, it falls back to that token. To set the key up, once:

1. Make a key pair without a passphrase, outside the repository:
   ```bash
   ssh-keygen -t ed25519 -N "" -C "essence release tags" -f ~/essence-release-tags
   ```
2. From the repository, add the public half as a deploy key with write access, and the private half as the Actions
   secret `RELEASE_DEPLOY_KEY`. On github.com, that's **Settings → Deploy keys → Add deploy key** (**Allow write access**
   checked), then **Settings → Secrets and variables → Actions → New repository secret**. With the GitHub CLI:
   ```bash
   gh repo deploy-key add ~/essence-release-tags.pub --allow-write --title "Release tags (publish workflow)"
   ```
   ```bash
   gh secret set RELEASE_DEPLOY_KEY < ~/essence-release-tags
   ```
3. Delete both files; GitHub has what it needs:
   ```bash
   rm ~/essence-release-tags ~/essence-release-tags.pub
   ```

The next run on `main` uses it: its `tag` job log says `set … ` for each tag, with no warning about the deploy key. The
key can push anything to the repository, so it lives only in that secret, which pull requests from forks don't get. To
replace it, delete the deploy key and the secret, and set them up again.

The web image's leg then runs `tools/Essence.Release notes`: the version is the first day tag that contains the commit,
and each entry is dated by the first day tag whose tree has its file, so rewriting `main`'s history doesn't move old
entries. It writes `release.json` (version, commit, months) and one `changelog-<month>.json` per month, which nginx serves
under `/release/`. On `main`, the build fails rather than ship a web image without a version. A pull request's build has
none: the dialog says *Development version* and lists the pull request's new entries as not released yet. A local Docker
build has no release notes at all (the same *Development version*, with nothing listed), and a local `notes` run takes the
version of the first day tag containing HEAD, if there's one.

**Open tabs.** A tab reads `release.json` again every 10 minutes, and when it's brought back into view (at most once a
minute, unless it was away with the offer showing), to compare its commit with the one it started with (`Essence.UI`'s `Services/UpdateCheck.cs`). The commit tells
releases apart, since one day's version can be deployed more than once, and any other commit counts, so a rollback reaches
open tabs too. While the tab is in use, a chip offers to reload. The tab reloads by itself only once it has been out of
sight for a minute, or as it's brought back from that (within 2 seconds), and only right after a read that saw the other
release, so a tab that lost its network isn't reloaded into an error page. The reload reopens on the same map view, fuel
and panels. A build without release notes, or without a commit, never checks.

To try the dialog with real notes while working on the web app (the folder is ignored by git and Docker):

```bash
git fetch --tags --force
```

```bash
dotnet run --project tools/Essence.Release -- notes src/Essence.Web/wwwroot/release
```

With those notes, `dotnet watch` checks for new releases too: after a commit, running `notes` again makes an open tab offer
to reload. Keep DevTools' *Disable cache* on, as the development server doesn't ask browsers to revalidate the file.

## Configuration

Every setting can be overridden with environment variables (`Section__Key`).

| Setting | Default | |
|---|---|---|
| `Feed:Url` | the Régie URL | |
| `Feed:PublishInterval` | `00:05:00` | |
| `Feed:InitialDelay` / `MinDelay` / `MaxDelay` | 40 s / 20 s / 120 s | Learned delay bounds. |
| `Feed:DelayPercentile` / `DelayHistorySize` | 20 / 50 | |
| `Feed:FastPollInterval` / `FastPollDuration` / `SlowPollInterval` | 10 s / 3 min / 60 s | |
| `Feed:Jitter` | 2 s | |
| `Feed:RequestTimeout` / `RetryCount` | 30 s / 2 | Per attempt, with `Microsoft.Extensions.Http.Resilience`. |
| `Feed:MaxImportAttempts` | 3 | Failed imports of one file before it is skipped. |
| `Backfill:Enabled` | `true` | Backfill missed versions from xlsx. |
| `Backfill:BaseUrl` | `https://regieessencequebec.ca/data/` | Folder of the xlsx files, ending with `/`. |
| `Backfill:MaxAge` | `01:10:00` | Older slots aren't requested (the server keeps the files about an hour). |
| `Archive:Path` / `RetentionDays` | `/data/archive` / 30 | |
| `Health:MaxImportAgeMinutes` | 15 | API `/health` threshold. |
| `Routing:BaseUrl` | `http://routing:5000/` | The routing server (OSRM), ending with `/`. 3 s per attempt and one retry. |
| `Routing:StatusUrl` | `http://routing:9090/` | The routing container's status server, ending with `/`. |
| `Geocoding:BaseUrl` | `http://geocoding:2322/` | The address search (Photon), ending with `/`. 1.5 s, no retry. |
| `Geocoding:StatusUrl` | `http://geocoding:9090/` | The geocoding container's status server, ending with `/`. |
| `RateLimiting:PermitLimit` / `WindowSeconds` | 120 / 60 | Requests per client IP and window, the address search's aside. |
| `RateLimiting:Search:PermitLimit` / `WindowSeconds` | 60 / 60 | `/places/` requests per client IP and window. |

The routing and geocoding containers take environment variables: `REFRESH_DAYS` (30; 0 never refreshes), and
`ROUTING_PBF_URL` (Geofabrik's `quebec-latest.osm.pbf`) or `GEOCODING_DUMP_URL` (Photon's dump of Canada), each with an
`.md5` beside it.
