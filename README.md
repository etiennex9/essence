# Essence

Continuously ingests Québec fuel prices published by the Régie de l'énergie
(`https://regieessencequebec.ca/stations.geojson.gz`, about 2,460 stations, regenerated every 5 minutes)
into PostgreSQL, keeping the station list and a full price-change history, serves them through a read-only HTTP API,
and shows them on a map of nearby stations.

| Project | What it does |
|---|---|
| `src/Essence.Data` | EF Core model, migrations, `StationKey` normalizer. Shared by every app. |
| `src/Essence.Ingest` | Worker: polls the feed, archives each new file, diffs it against the database and writes only the changes. Backfills missed versions from the Régie's xlsx files. Fetches stations' opening hours once a week and matches them to the stations (see [Opening hours](#opening-hours)). |
| `src/Essence.Api` | ASP.NET Core minimal API (read-only DB role), OpenAPI + Scalar UI. |
| `src/Essence.Migrator` | One-shot: applies migrations, creates the `essence_writer` / `essence_reader` roles and grants them. |
| `src/Essence.Contracts` | The API's response records and the release notes', shared by the API, the UI and the release tool. |
| `src/Essence.Ranking` | Which station to fill up at: costs, savings, the pick and its rules, the search area; for trips, the route's corridor, km along it, detours and the range; the OSRM and Photon clients, the address search's labels, and Québec's boundary; which source's opening hours a station shows (`HoursPrecedence`). Only `Essence.Hours` as a dependency; used by the API. |
| `src/Essence.Hours` | Stations' opening hours: OpenStreetMap's `opening_hours`, which every source's hours are put in, read (refusing what can't be told for sure) and written one way; Québec's time zones and holidays; whether a station is open, closes soon or is closed when the driver gets there. No dependencies, and trimmable for the web app. |
| `src/Essence.Routing` | The routing image: OSRM with Essence's car profile, which builds and refreshes Québec's road data itself (see [Routing and address search](#routing-and-address-search)). |
| `src/Essence.Geocoding` | The address search image: Photon, which builds and refreshes its index of Québec's places itself. |
| `src/Essence.Supervisor` | The supervisor both of those images run: it builds their data, serves it, refreshes it, retries, and reports its progress at `:9090/status`. |
| `src/Essence.UI` | Razor class library: the map page, the station details pane and everything behind them, stations' hours included (with `Essence.Hours`). Shared by the web app and the Android app. |
| `src/Essence.Web` | Blazor WebAssembly host for `Essence.UI`, served as static files by nginx. See [its README](src/Essence.Web/README.md). |
| `src/Essence.Mobile` | The Android app: a .NET MAUI Blazor Hybrid host for `Essence.UI`, with home-screen widgets. Not in `Essence.slnx`: see [its README](src/Essence.Mobile/README.md). |
| `tools/Essence.Release` | Run by the publish and Android workflows: dates each release by the day it landed on `main` and writes the apps' changelog (see [Versions](#versions)). |
| `tests/Essence.Ingest.Tests` | xUnit unit tests: parsing (JSON and xlsx), diffing, scheduling, backfill planning, fetch decisions, archive. The hours: Costco's locator against a trimmed real answer, the curated rules, days and times as the locators write them, matching, the gate, the weekly night schedule across daylight time and the retries, the rows' diff, the cache, the runs' history, the settings. |
| `tests/Essence.Ranking.Tests` | xUnit unit tests: fill costs, the Best near you ranking and its rules, the search area, eligibility; polylines, the corridor and km along, the trip's candidates, detours, range and ranking; the OSRM and Photon clients against stub servers; the address search's labels and filtering; Québec's boundary at its borders; which source's hours a station shows, stale sources and what's trusted; the stations left out because they'd be closed on arrival (trusted hours only, holidays, corrected hours, everything closed), and the rankings' reference and first station reached once they are. |
| `tests/Essence.Hours.Tests` | xUnit unit tests: the sources' hours, strict and lenient, what's refused and why, the canonical rule and its round trip, the week's groups; open, closing soon and closed, past midnight and across daylight time; the holidays, Easter and the cautions; the time zones at their borders, and Canada's rule against the system's zones. |
| `tests/Essence.Api.Tests` | xUnit tests of the API in memory (WebApplicationFactory, no database): the address search's endpoints against a stub Photon, their rules, rate limit and logs; the hours sources' status and health check, from runs given (`/hours/sources` itself reads the database, so only its place in the OpenAPI document is checked); the stations' hours as served, when the snapshot loads them again, and the 304 rule with the hours; `ignoreHours`, the stations said to be left out, the 304 rule of an answer that depends on the clock, and the Development clock (`Clock:Now`); `/client-config` with and without an OpenStreetMap application, and the stations' OpenStreetMap elements. |
| `tests/Essence.UI.Tests` | xUnit unit tests: marker tones and loading, price history series, brands, formatting, the API client, car profiles, Best near you's refreshes, the station shown in Best near you and on the way, the open trip and saved places, the address search's debounce and cache, pins' addresses, the route's split at the range, Google Maps links, the release notes, translations; stations' hours as shown (open, closing soon, closed, the week, the source) in the station's time zone, closed markers, the hours on the Favorites widget and rows; what Best near you, Best on the way and their Go links say of a station closing soon after the driver gets there or closed then, the stations left out, the hours on the Best near a place and frequent route widgets, `ignoreHours` sent, and plans read and restored without the new fields; hours corrected on the device (kept per server, read leniently, winning everywhere, `ignoreHours` asked again with them), the editor's week and rule, and OpenStreetMap: the edit sent against a stub API (only the hours changed, escaping, ways, a version conflict, the changeset always closed, brand hours never sent on their own), linking with PKCE and the state checked, and the Android app's `essence://` links. |
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
curl localhost:8080/hours/sources
```

Look at the data with `docker compose exec postgres psql -U essence_admin essence`, and at the raw archive with
`docker compose exec ingest ls -R /data/archive`, and at the hours sources' last downloads with
`docker compose exec ingest ls -R /data/hours`.

## Develop

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

`dotnet test` runs on Microsoft.Testing.Platform (opted in via `global.json`). Both leave out the Android app, which needs
the MAUI Android workload: [src/Essence.Mobile](src/Essence.Mobile/README.md) says how to build it.
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

To check stations' hours at another time of day, add `?now=` and a moment with its offset to the page's address, e.g.
<http://localhost:5173/?now=2026-11-11T21:15:00-05:00>: the app's clock starts there and moves on with the real one. Only
a Debug build in the Development environment reads it (`src/Essence.Web/SimulatedTime.cs`); the prices' refreshes keep the
real clock. The API has its own: in the Development environment, `Clock__Now=2026-11-11T21:15:00-05:00` starts its clock
there, moving on with the real one, so `/stations/best` and `/trips/best` leave out the stations closed at that time of day
(`src/Essence.Api/SimulatedTime.cs`); elsewhere it's ignored. Give both the same moment to see the app and the API agree.

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

## Opening hours

The feed says every station is "En opération", so whether one is open has to come from its hours. A second worker in the
ingest (`src/Essence.Ingest/Hours`) fetches them from the brands and OpenStreetMap, matches them to the feed's stations,
and keeps one row per station and source. Every source's hours are put in OpenStreetMap's `opening_hours`, made
canonical by `Essence.Hours` (`Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30`); hours a source writes in a way that can't be read
for sure leave that station out, with a warning in the log, rather than being guessed. The API serves one source's hours
per station (see **Which hours show**), and the app shows them.

| Source | What it fetches | Matched to |
|---|---|---|
| `costco` | Costco's warehouse locator (`costco.ca/AjaxWarehouseBrowseLookupView`), asked from Montréal and from Québec City, each answering its 50 nearest warehouses: the gas bars' hours (`gasStationHours`) of the Québec warehouses that have one. | The feed's Costco stations within 600 m: the feed's point and the locator's can be 540 m apart, the gas bar being away from the warehouse. |
| `curated` | [curated-hours.json](src/Essence.Ingest/Hours/curated-hours.json), shipped in the image: rules written by hand, each with the day it was checked and where. | A brand's rule: every station of the brand. A station's: its address and postal code, as in the feed. It beats the brand's. |
| `ultramar` | Ultramar's locator on journie.ca (`/ultramar/api/locations/nearest`), one request for all of Canada's ~620 stations: each station's week (`siteRegularHours`, `siteTwentyFourSeven`) and its services', the forecourt's first, and whether it's closed for now. | The feed's Ultramar stations within 150 m (326 of 356 on 2026-11-13), or within 2 km at the same house number and postal code (6 more, the locator's point 165 to 660 m off). |
| `circlek` | Circle K's store locator (`api.circlek.com/us/ngrp-store-locator/v1/stations`), asked about Québec's west and east: Couche-Tard's ~605 Québec stores, each with its store's hours (`openingHours`: `alwaysOpen`, or each day's `open` and `close`). | Only the station whose feed name carries the store's number: "Couche-Tard #140" is the store 6000140, whatever the station's brand and however far (467 of the 473 stations named so on 2026-11-13, 8 of them 336 to 716 m away; the other 6 stores weren't in the locator). Never by distance: the store nearest a station is another station's 74 % of the time. |
| `shell` | Shell's locator (`shellgsllocator.geoapp.me/api/v2/locations`): its lists of Québec's ~400 stations (`within_bounds`, a cluster listed in turn until it gives stations), then the details (`forecourt_opening_hours`, else `opening_hours`; `open_status`) of only those within 150 m of a feed Shell station, and, for the 14 feed Shell stations with none there, those within 2 km at the station's house number and postal code as the list gives them (a few a week). | The feed's Shell stations within 150 m (371 of 391 on 2026-11-13), or within 2 km at the same house number and postal code. |
| `osm` | OpenStreetMap, from an Overpass server: every `amenity=fuel` element in Québec (~2,860), with its tags and point (`out tags center`), never who mapped it. Each is a record, hours or not (~184 have `opening_hours`), so a station knows which element to edit. | The feed's stations of the element's brand (`brand`, else `operator`, else `name`) within 150 m, Costco's within 600 m, or within 2 km at the same house number and postal code (1,765 elements on 2026-11-13, 134 of them with hours). Then, without its hours, an element of another brand or none that's on a station still without one (351 more, see **Matching**). |

- **Costco's terms of use ban automated collection.** The owner accepted that risk at two requests a week, one per point.
  A request isn't repeated within a run (`Hours:RetryCount` 0), and a run that fails stops at the request that failed,
  to be tried again later: a week without failures makes two requests, and while the locator fails, each retry (see
  **When**) makes at most two more. Its holiday hours (`upcomingHolidays`) are the warehouse's, and whether the gas bar follows
  them isn't known, so they're ignored for now. The curated rule (the same hours at all 18 Québec warehouses, checked
  2026-11-11 and confirmed for Laval) serves when the locator can't be had: it's a source of its own, so a Costco station
  has both rows, and which one counts is the API's choice.
- **The other brands' terms, and their requests.** Each source runs once a week (see **When**), its requests a few
  seconds apart (`Hours:MinPause` 2 s to `Hours:MaxPause` 3 s, drawn at random), and none is repeated within a run except
  as said here. Petro-Canada's, Esso's and Canadian Tire's locators are behind bot protection, which isn't worked
  around: their stations get OpenStreetMap's hours, or none.
  - `ultramar`: one request a week. journie.ca's robots.txt allows everything. A station is closed for now when its
    forecourt is (`serviceStatus` "temporary closure"), or the station is (`siteStatus`) and has no forecourt. But the
    forecourt's status says little: in November 2026 it was "active" at every Québec station, the 7 whose `siteStatus`
    (and store) said "temporary closure" included, and of the two of those the feed has, one's prices kept moving while
    the other's hadn't moved in a month. So a station closed for now whose forecourt is active is left out, with a
    warning, rather than taken either way: it gets another source's hours (OpenStreetMap's), or none. A station said to
    be open 24 hours whose days say otherwise, or whose days are "00:00" to "12:00" (what the locator gives on days it
    doesn't say, the weekend of a station open 6 to 18 on weekdays), is left out too: which is right can't be told.
  - `circlek`: two requests a week. Circle K's terms say nothing about automated use. Its hours are the store's: an
    Ultramar's or a Shell's hours outrank them where both match one station (see **Which hours show**). A store that
    gives no hours, or not every day's, is left out.
  - `shell`: about 22 lists and 375 details a week, one every 2 to 3 s, so a run takes about 17 minutes. A detail that
    fails (an error, no answer in time, a body that isn't a station's) is asked again once, after the pause; failing
    twice, its station is left out with a warning, like a station gone from the locator (404), so one broken record
    can't keep the run failing, and retried, week after week. 3 details failing one after the other fail the run (the
    locator is down, or refusing it): asking on would only hammer it. Shell's terms say nothing about automated use. A
    station's days are ranges (`["Mon", "Thu"]`, `["Sat", "Sun"]`).
  - `osm`: one query a week (`Hours:Osm:Query`, about a minute of the server's time; `Hours:Osm:RequestTimeout` 4 min),
    to the next server in `Hours:Osm:Endpoints` when one is busy (a 504, an error in its answer), too slow, or answers no
    elements at all (a server without its areas, or rebuilding them, answers so: Québec never has no station). Overpass
    answers 406 to a generic User-Agent, so `Hours:UserAgent` is sent. **OpenStreetMap's data is © OpenStreetMap
    contributors, under the [ODbL](https://www.openstreetmap.org/copyright)**: where the app shows these hours, their
    source line says OpenStreetMap ("OpenStreetMap, checked 2025") and links the element. Hours that can't be read for
    sure, even leniently, leave the element's record without them (a warning in the log). When the hours were checked
    is `check_date:opening_hours`, else `check_date`; an element's last edit isn't a check.
- **When.** Each source runs once every 7 days, at a minute drawn at random in that night's 01:00–05:00 (Québec time,
  the window an hour shorter or longer on the nights the clocks change), drawn again each week. After a start, a source
  that never succeeded runs a minute after the one before it, and `curated` runs on every start, so a new image's rules
  are in at once. A failed or rejected run is tried again after 15 minutes, then 1 hour, then every 6 hours until one
  succeeds.
- **Matching.** A station's own rule comes first, then Couche-Tard's stores by the number in the station's name (whatever
  the station's brand; a number two stations carry matches neither), then the records with a point (by distance, then by
  address), then a brand's rule for the brand's stations left, and last, for OpenStreetMap only, an element of another
  brand on a station (by location).
  - **By distance.** A record with a point only ever matches stations of its own brand (folded: "Pétro-Canada" and
    "Petro Canada" are one; Esso and Mobil are one, Couche-Tard and Circle K too, and R.L. and the names OpenStreetMap
    gives its stations, "Petroles RL" and Rezzo), and a Costco record, whatever its source, within 600 m. A match by
    distance is confirmed when the postal codes or the house numbers agree (Drummondville's two points, 540 m apart,
    have other postal codes but the same number). Pairs are taken one to one, the confirmed first, then the closest, and
    a pair that isn't confirmed is left out when the same record is within reach of another station, or the same
    station within reach of another record with other hours: which is which can't be told then.
  - **By address.** A record and a station of the same brand both left free then match when the house number **and** the
    postal code agree, up to 2 km apart: one of the two points is off. Ultramar's locator puts 6 stations 165 to 660 m
    from where they are (5 more are 3.5 to 8.9 km off, past the 2 km), and the feed draws a few stations a street or a
    village away. A postal code alone is never enough: a rural one covers villages kilometres apart. A record at the
    address of two stations, or a station with two such records, matches neither.
  - **By location.** OpenStreetMap is often years out of date: of the elements by stations it didn't match in November
    2026, a third were last edited in 2020 or before, many still under a brand the station no longer has (Saguenay's
    Nutrinor stations are Sonic's there). So a station still without an element gets its nearest one of another brand,
    or none, when it's within 50 m, no other feed station (of any brand, matched or not) is within 150 m of the element,
    and no other free element is within 50 m of the station. The element's hours aren't taken, its brand suggesting
    they're as old: the row (`location`, never confirmed, no rule) only says which element a correction made in the app
    goes to. Nutrinor Chicoutimi on boulevard Saint-Paul stays without one: its element is 62 m away, and 54 m from the
    Petro-Canada next door.
- **Checks.** A run whose source gives no records, or that matches fewer stations than 80 % of the last good run, is
  rejected: nothing is written, and the last good hours keep serving. The stations matched by location only don't
  count, on either side: they carry no hours, and elements that lost their brands (a changed query, a broken answer)
  would otherwise match by location what they matched by brand, and pass. `curated` is taken as written instead, fewer
  rules or none included (a rule taken out of the file takes its rows with it), unless one of its rules is left out (it
  can't be read, or it's there twice): then the whole file is rejected until it's fixed, and the rules before it keep
  serving.
- **The last downloads.** Each source's last good download is kept in `/data/hours/<source>/` (`latest.raw`, the one
  before as `previous.raw`, and `meta.json` with its version and when it was fetched), cut down to the fields read:
  nobody's name or phone number (for OpenStreetMap, neither who mapped an element nor an operator or a name that's no
  feed station's brand, which can be a person's). Every hour, when the feed's stations changed, they're matched again
  without asking the sources, so a station added or moved gets its hours within the hour (Shell's only from the stations
  whose details the last run asked for). Only the download whose rows are serving is matched again: one left older on
  disk (keeping the newer one failed) waits for the next run.
- **Tables.** `station_hours` has one row per station and source: the rule (null when a source has the place but not its
  hours, or doesn't give them), the source's id for the place and a link, how it was matched (`natural_key`,
  `store_number`, `distance`, `address`, `brand` or `location`) and how far, whether that's confirmed, whether the
  source says it's closed for now, when the source says it was checked, and when it was fetched. `hours_imports` logs
  each source's runs (`matched` leaving out the matches by location only): `succeeded`, `unchanged`, `rematched`
  (matched again from the last download because the stations changed: nothing was fetched, so it's no success),
  `rejected`, `failed` or `disabled` (see **Turning a source off**), with the version of the download (its SHA-256), its
  counts and the error. The feed's `imports` is left alone: what reads it takes every success there for a version of the
  feed.
- **Turning a source off.** On a start, the rows of a source that has run but is now off (`Hours:<Source>:Enabled`
  `false`) or gone from the image are removed, since nothing refreshes them any more, and a `disabled` run is logged:
  the source is no longer listed in `/hours/sources` or checked by `/health`. Turned on again, it starts over, as a new
  source, and runs soon after the start. `Hours:Enabled` `false` only stops the worker: the rows stay as they are,
  and go stale.
- **Health.** `/hours/sources` says how each source is going, and how many stations it has hours for (an OpenStreetMap
  element without them, or matched by location, isn't counted); `/health`'s `hours` check is `Degraded` when a source
  that has run has had no success in `Health:HoursStaleAfterDays` (21) days, or none ever. Its hours still show then,
  but are stale.
- **Which hours show.** The API keeps each station's chosen hours with its snapshot of the stations (`HoursPrecedence`
  in `src/Essence.Ranking`): Costco's own, then `curated`, then Ultramar's and Shell's forecourts' (`ultramar`,
  `shell`), then Couche-Tard's stores' (`circlek`: a Couche-Tard that sells Shell's gas shows the pumps' hours), then
  OpenStreetMap's (`osm`, confirmed matches first). A stale source comes after every fresh one, so a stale Costco gives
  way to the curated rule; `curated` never goes stale. A row without a rule, or with one that can't be read, doesn't
  count. The hours are `trusted` when they're a brand's or Essence's from a fresh source: only those will ever leave a
  closed station out of Best near you. OpenStreetMap's, and a stale source's, are only shown. The snapshot is loaded
  again when an hours run changed rows (`succeeded`, `rematched`, `disabled` with rows removed), when a success makes a
  stale source count again (`unchanged`), and when a source goes stale.
- **In the app.** The hours are read in the station's own time zone (Eastern, or Atlantic on the Îles-de-la-Madeleine),
  never the device's. A station's details say "Open until 9:30 p.m." (amber in its last 30 minutes), "Closed · opens
  6:00 a.m.", "Open 24 hours" or "Temporarily closed" on the line under its address, warn when a holiday's hours may
  differ, and list the week with where the hours come from, or "Hours unknown". The map's pill of a station closed now is
  grey, and gets room for its price after the open ones; Favorites and the comparison say "Closed", or "Closes 9:30 p.m."
  in its last 30 minutes, and the Favorites widget says when a favorite closes within 90 minutes of its refresh, or that
  it's closed.
- **On arrival.** Best near you and Best on the way check each station's hours when the driver would get there: now
  plus the drive there (`etaMin`, or `toMin` from a trip's start). A station whose hours are `trusted` and say it's closed
  then, or closed for now, is left out before the ranking, so the closest (or the first on the way) that savings are
  counted against is one that's open (the stations likely closed don't count among the closest measured by road); hours that aren't trusted, unknown ones, a holiday whose hours the
  rule doesn't state, and stations the device corrected (`ignoreHours`) are never left out, and when every station would
  be, none is. The pane then says so in one muted line at its end ("Costco left out: closed by the time you'd get there
  (closes 9:30 p.m.)"), for at most 2 of them, only those cheaper than the pick. A station that closes within 30 minutes
  of the arrival keeps its place, with "· Closes 9:30 p.m." on the hero's address line, "Closes at 9:30 p.m., about 8 min
  after you get there." before why it's shown, and a "Closes 9:30 p.m." tag on its rows and tiles; one closed then (only
  when nothing could be left out) says "Closed by the time you get there" in amber instead of its saving. Before going to
  either, Go and the details' Directions ask first ("Costco closes at 9:30 p.m. You'd get there around 9:22 p.m."), with
  Go anyway as the real link. The arrival is worked out again at each render, and the pane renders again when time
  moves a shown station to closing soon or closed, so a plan kept in the tab doesn't go stale; the details use Best's ETA when they were opened from it, or the straight line's estimate. The Best near a place
  and frequent route widgets say when the pick closes or that it's closed when you get there, as the Favorites widget does.
- **Corrections on the device.** Under a station's hours, **These hours are wrong** (or **Add the hours**, when they're
  unknown) opens an editor: each day closed or open from one time to another (a closing time earlier than the opening is
  the next day's; the same time twice, all day), or **Open 24 hours**, starting from the hours shown. Saved, the hours
  are kept on the device (`essence.hours`, per server, as favorites are: a station's id is its row in that server's
  database), and **the device's hours always win**: the details ("Your hours" as their source), the map's pill,
  Favorites and the comparison, Best's arrival texts and the Go links' warning, and the widgets go by them. Best near you
  and Best on the way send the corrected stations' ids as `ignoreHours` (the latest 50), so the server never leaves out
  one the user says is open, and check the arrival on the device with the user's hours: one they say will be closed
  then shows so (in amber, and Go asks first), as a closed station the server couldn't leave out does. Saving or undoing a
  correction asks Best again (in the background) and plans an open trip again. **Use the station's hours** forgets it.
- **Sending the correction to OpenStreetMap.** With the deployment's OpenStreetMap application set (`Osm:ClientId`, see
  [OpenStreetMap edits](#openstreetmap-edits)), the editor and About offer to link the user's **own** OpenStreetMap
  account: OpenStreetMap's rules want edits from the person who makes them, so there's no shared account. The link is
  OAuth 2 with PKCE, from the app itself (a window of its own on the web, a browser tab on Android), and the token stays
  on the device (localStorage on the web, Android's secure storage, left out of its backups): the Essence server never
  sees it, and there are still no Essence accounts. Once linked, and when the station was matched to an OpenStreetMap
  node or way (`osmElement`), **Also update OpenStreetMap** sends the hours on Save: the element as it is now, a changeset
  (`created_by=Essence <version>`, the comment "Update opening hours of <name>", and `source=survey` only when **I
  checked these at the station** is ticked), the element with only `opening_hours` and `check_date:opening_hours` (today,
  in the station's zone) changed, every other tag as it was, then the changeset closed, which happens whatever went wrong
  once it was open. A version conflict (someone edited it meanwhile) fetches it again and tries once more. The app then
  says "Sent to OpenStreetMap" with a link to the changeset, or OpenStreetMap's reason; the correction stays saved on the
  device either way. **A brand's hours are never sent on their own:** hours equal to the ones shown from a brand (or
  Essence's own) leave the box unticked, and can't be sent until the user says they checked them at the station; equal to
  OpenStreetMap's own, sending only says when they were checked, so the box starts unticked. A relation isn't edited
  (the editor links to OpenStreetMap's own editor instead), and a station without an element gets **Add it on
  OpenStreetMap**, a link to the map at that spot: the app never creates one, to avoid duplicates. Other devices get the
  fix at the next weekly OpenStreetMap run, where no brand's hours outrank it.

## API

| Endpoint | |
|---|---|
| `GET /stations?region&brand&active&q&page&pageSize` | Stations, paged. `q` searches the name, address and postal code. |
| `GET /stations/{id}` | One station with its current prices, opening hours (`hours`, null when unknown) and OpenStreetMap element (`osmElement`: `node/123`, `way/456`, `relation/789`, its hours or not; null when it wasn't matched to one). |
| `GET /stations/nearby?lat&lon&radiusKm=5&fuel&limit=20` | Active stations within a radius, closest first, with their opening hours. |
| `GET /stations/best?lat&lon&fuel&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&radiusKm=10&ignoreHours` | Where to fill up near a point: the pick, the closest, the cheapest and a few others that save, and the stations left out because they'd be closed on arrival (see below). |
| `GET /trips/best?fromLat&fromLon&toLat&toLon&fuel&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&rangeKm&spareKm=30&ignoreHours` | Where to fill up on the way to a destination: the route, where to stop, the stations along it ranked by detour, and those left out because they'd be closed on arrival (see below). |
| `GET /places/search?q&lat&lon&lang=fr` | Places in Québec matching what was typed, at most 5, those near `lat`,`lon` first (see below). |
| `GET /places/reverse?lat&lon&lang=fr` | What a point in Québec is called: the nearest address, street or named place. |
| `GET /services` | Whether trips and the address search are ready, how far their data is (`state`, `stage`, `percent`), or when a failed build tries again (`lastError`, `nextAttemptAt`). 60 a minute per client IP, apart from the rest. |
| `GET /stations/{id}/prices?fuel&from&to` | Price history overlapping a range (default: the last 30 days). |
| `GET /prices/current?fuel&region&brand&sort=price&includeUnavailable&limit=50` | Current prices, cheapest first. |
| `GET /imports?limit=20` | Recent imports with their statistics and `source` (`feed` or `xlsx`). |
| `GET /hours/sources` | Each source of stations' opening hours: its last success and attempt, what came of it and why, its failures in a row, how many stations it has hours for, and the version of its data (see [Opening hours](#opening-hours)). |
| `GET /client-config` | What the apps need of the deployment's settings: its OpenStreetMap application's client id (`osmClientId`, null when editing OpenStreetMap is off), and where it signs in and edits (`osmAuthUrl`, `osmApiUrl`). The Android app picks its server at run time, so it reads them here. See [OpenStreetMap edits](#openstreetmap-edits). |
| `GET /health` | 200 when the database answers and the last successful import is under 15 min old, else 503. A routing or geocoding server that doesn't answer, or whose data is being built, makes it `Degraded`, still 200, with the build's stage and percent; so does an hours source without a success in 21 days, with each source's last success. |

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

Both leave out, before ranking, the stations that will be closed when the driver gets there (see **On arrival** in
[Opening hours](#opening-hours)): at now plus `etaMin` (one way, by road or estimated) for `/stations/best`, now plus
`toMin` (from the start, by road) for `/trips/best`; each station carries its `hours` and that time. `leftOut` lists at
most 2 of those left out, only those cheaper per litre than the pick, lowest fill-up cost first, each with its `hours`
and `etaMin`; it's null when none was. `ignoreHours` takes the ids of stations whose hours the device corrected (at most
50, separated by commas): they're never left out for the server's hours.

`/places/search` finds addresses, streets, towns and named places in an index of Québec's places from OpenStreetMap
(Photon, in the geocoding container), within Québec's box and near `lat`,`lon` when they're given; places whose province
isn't Québec are dropped, each place comes once (a long street's stretches in one town are one), and a business found
only by its address comes after the address. `q` takes 3 to 100 characters; `lang` is `fr` or `en`. `/places/reverse`
answers 422 for a point outside Québec's boundary (known without Photon), 404 when nothing with an address is within
1 km, else the nearest address within 100 m, or the nearest street or named place. Both answer 503 when Photon is down;
they wait 1.5 s for it, without retrying.

While the routing data or the address index is built (or loaded, after a refresh), the API reads the containers'
progress (`:9090/status`, asked every 5 seconds in the background) instead of calling them: `/stations/best`
estimates, and `/trips/best` and `/places/*` answer 503 with `service`, `state`, `stage`, `percent`, `lastError` and
`nextAttemptAt` in the problem, as `/services` says them. A container whose status can't be had is called as before.

`/stations/{id}`, `/stations/nearby`, `/stations/best` and `/trips/best` send `Last-Modified`: when the latest import was fetched. The map refreshes its prices
about 5 min 20 s after that time, which is when the next import should be in, and more often while that import is late. A request
whose `If-Modified-Since` is still current gets a 304, unless the hours changed after it: they change apart from the
feed, so `Last-Modified` stays the feed's, but the latest hours run (one that changed rows, or any success, which can end
a stale spell) and the latest moment a source went stale, rounded up to the second, must be no newer either.
`/stations/best` and `/trips/best` depend on the time too (a station closing changes them with no new data): they
give no 304 once the latest import is more than 10 minutes old.

`hours`, on `/stations/{id}`, `/stations/nearby`, and the stations of `/stations/best`, `/trips/best` and their
`leftOut`, is the station's opening hours: `rule`, in OpenStreetMap's `opening_hours` as `Essence.Hours` writes it
(`Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30`, `24/7`) and in the station's time zone; `source` (`costco`, `curated`,
`circlek`, `ultramar`, `shell` or `osm`; a client shows one it doesn't know as it is); `trusted` and `confirmed` (see
[Opening hours](#opening-hours)); `checkedAt`, when the source says they were last checked; `link`, where to check them;
and `temporarilyClosed`.

Fuel names accept or omit accents (`Regulier` = `Régulier`). Errors are ProblemDetails. Requests are rate limited per client IP
(`RateLimiting:PermitLimit` per `RateLimiting:WindowSeconds`, 120/60 s by default); `/places/` has a window of its own
(`RateLimiting:Search:PermitLimit` per `RateLimiting:Search:WindowSeconds`, 60/60 s), so typing doesn't eat the rest. The `X-Forwarded-For`, `-Proto` and `-Prefix`
headers are trusted from private networks only, where the web container that forwards `/api/` lives; that container in turn
takes the client's address from a reverse proxy on a private network. Allowed CORS origins come from `Cors:AllowedOrigins`:
in Compose, `CORS_ORIGIN` from `.env`. The web container needs none, since the map calls the API through its own `/api/`.

### API compatibility

The web app always runs the UI its server serves, but an installed Android app runs the UI it was built with against the
live API, for as long as it isn't updated, and a newer app may meet an older self-hosted API. So the API's contract (its
paths, parameters and answers, `src/Essence.Contracts`) only grows:

- **Allowed:** new endpoints; new optional parameters whose default keeps the old behaviour; new response fields that are
  nullable or have a default.
- **Never:** removing or renaming a field or a parameter; changing its type, unit or meaning; making a parameter
  required; narrowing the range a parameter accepts.
- **Clients** tolerate values they don't know in enum-like strings (a fuel, a `state`, a reason): they show them as they
  are or leave them out, and don't fail.
- **A breaking change** gets a new path, and the old one keeps answering as before while installed apps use it.
- **To retire old apps**, the API can answer `426 Upgrade Required` to their `User-Agent`,
  `Essence-Android/<version name> (<version code>)` (e.g. `Essence-Android/2026.11.07.2 (449482)`): the app then asks to be
  updated. Compare the version code, which grows with every build; a version name can have a fourth part.

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
`Directory.Packages.props` ([select-images.sh](.github/scripts/select-images.sh)). A manual run, or a change to the publish
workflow, its script or `.dockerignore`, builds all six. The web image is also built along with any other, and when a
changelog entry changed: it carries the release's version and changelog ([Versions](#versions)). Pull requests build the
images they affect, without pushing. The Android app has a workflow of its own, which runs after this one
([Android app releases](#android-app-releases)).

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
[truenas-autodeploy.py](deploy/truenas-autodeploy.py) every 5 minutes: when a tag the app uses points to a newer image than
the app runs (`latest` on GHCR, or `postgres:18` on Docker Hub), it waits until the whole release is published, snapshots
the database's dataset, then pulls and
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
- **PostgreSQL** is rolled out the same way when Docker Hub moves `postgres:18`: PostgreSQL 18's minor releases and
  rebuilds of its image, never 19, since the tag decides how far an image moves. The script checks Docker Hub with HEAD
  requests, which don't count against its pull limits.
- **To hold an image back**, set it to `:sha-<commit>` in the app's YAML (**Apps → essence → Edit**). A sha tag doesn't
  move, so the script leaves it alone until you set it back to `:latest`. Only the images a push rebuilt get its sha tag:
  pick one listed on the image's package page (the repository's **Packages**). Holding `web` back also keeps the version
  and changelog it shows. To hold PostgreSQL at a minor version, use its tag, e.g. `postgres:18.1`; to fix the image
  itself, pin its digest (`postgres:18@sha256:…`). To pause everything, disable the cron job.
- **Migrations only go forward**, so rolling back the images doesn't undo one. Stop the app. Roll the postgres dataset back
  to the `@autodeploy-` snapshot taken before the bad rollout: **Datasets** → the dataset → **Data Protection** → **View
  Snapshots** → that snapshot's row → **Rollback**. If newer snapshots exist, the default choice is refused: choose
  **Newer Clone**, which deletes the snapshots taken after it. Then, in **Apps → essence → Edit**, set the images to the
  `sha-<commit>` tags of the release before, **Save**, and **Start** the app: Save doesn't start a stopped app, and the
  cron job leaves a stopped app alone. Starting it on `latest` instead would run the new migration again.
- **To update the script**, look at what changed in `deploy/truenas-autodeploy.py`, since root runs it, then run the
  `curl` command again.

### OpenStreetMap edits

The apps send hours corrections to OpenStreetMap with each user's own account ([Opening hours](#opening-hours)), through
one OAuth 2 application that the deployment registers on openstreetmap.org. It's a public client (no secret: the app
proves itself with PKCE), so only its client id is configured, and the token each device gets never reaches the server.

1. Sign in to <https://www.openstreetmap.org>, then **My Settings → OAuth 2 applications → Register new application**.
2. Name it (Essence, or the deployment's name), and give it both redirect URIs, one per line:
   `https://<your domain>/osm-auth.html` (the web app) and `essence://osm-auth` (the Android app). OpenStreetMap compares
   them exactly: no trailing slash on the second.
3. Untick **Confidential application?** (a public client), and tick only **Modify the map** (`write_api`) and **Read user
   preferences** (`read_prefs`, which says who's linked). Register.
4. Copy its **Client ID** into the api's `Osm__ClientId` ([deploy/truenas.yaml](deploy/truenas.yaml), or `OSM_CLIENT_ID`
   in `.env` for Compose) and restart the api. `/api/client-config` then names it, and the editor and About offer to link
   an account.

**To test** without touching the real map, do the same on OpenStreetMap's development server,
<https://master.apis.dev.openstreetmap.org> (an account of its own; its data is a sandbox), and set `Osm__AuthUrl` and
`Osm__ApiUrl` to `https://master.apis.dev.openstreetmap.org` as well as its client id. Set both: the api doesn't start
with one on the development server and the other on OpenStreetMap's, nor with an address that isn't https (plain http
only on the machine itself), since the apps send a token only to its own OpenStreetMap's API and refuse a server that
names another. A token is kept with the OpenStreetMap and the API it's for, so switching the api between the two asks
each device to link again. Production OpenStreetMap is never used for tests.

To link from the web app on your machine, add its local redirect URIs too: `http://127.0.0.1:5173/osm-auth.html` for
`dotnet watch` and `http://127.0.0.1:8081/osm-auth.html` for Compose. OpenStreetMap takes plain http only for 127.0.0.1 and
::1, not `localhost` (nor a LAN address), and the sign-in comes back to the page's own origin, so open the app at
<http://127.0.0.1:5173> (or <http://127.0.0.1:8081>) to link. With `dotnet watch`, the api must allow that origin: set
`CORS_ORIGIN=http://127.0.0.1:5173` in `.env` for the api in Compose (an api run in Development allows both). A deployment
links only from the origin it registered, its https domain: not from its LAN address.

On the web, the sign-in opens in a window of its own, which comes back to `osm-auth.html` next to the page: OpenStreetMap
sends `Cross-Origin-Opener-Policy: same-origin`, which cuts that window off from the page, so the page hands the code back
through a `BroadcastChannel` of the app's origin (the page checks the state it sent before trading the code). The web
container doesn't log `/osm-auth.html`, whose query string has the code. The browser must let the window open: a blocked
one says so.

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
  it, which takes the server off for a few seconds, and keeps the previous data until the server answers from the new
  one (going back to it if it doesn't within 10 minutes, a restart in between included).
- rebuilds the data after an image bumps OSRM's or Photon's version (data built by another version may not load), or
  changes the car profile.
- retries a failed download or build, or new data the server doesn't start on, after 1 minute, then twice as long
  each time, up to 6 hours, and says when (the map: *couldn't be prepared. Trying again at 2:15 p.m.*); a restart
  throws a build in progress away, and a download resumes where it stopped. A server that keeps stopping is started
  again, and after 5 times in a row its data is rebuilt.
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
the list scrolls. The Android app shows its own version name there instead, which numbers a day's second release and
the next ones ([Android app releases](#android-app-releases)).

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
with it fails, it falls back to that token. The Android workflow pushes its releases' `android-*` tags the same way. To
set the key up, once:

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

### Android app releases

[The Android workflow](.github/workflows/android.yml) builds the Android app ([src/Essence.Mobile](src/Essence.Mobile/README.md))
and releases it on the repository's GitHub Releases page, where people download it and where installed apps look for
updates.

**When.** After each Publish run on `main` that succeeded, for a push or started by hand, whose `tag` job has dated the
commit by then; and when run by hand on `main` (**Actions → Android → Run workflow**), which builds `main`'s tip and so
needs Publish to have run for it. It builds only when the app's inputs changed since the commit the latest release was
built from, which that release's notes record in a hidden `<!-- essence-android-commit: … -->` line: `src/Essence.Mobile`,
`Essence.UI`, `Essence.Contracts`, the shared build files, or the workflow and its script, Markdown aside
([select-android.sh](.github/scripts/select-android.sh)). A change to the API alone doesn't rebuild the app. With no
release yet, or run by hand, it builds, even with nothing changed: that's a new release. Only `main`'s tip is released: if
`main` has moved on, before or during the build, the run after Publish's for the newer commit releases instead. A push of
only Markdown or `docs/` starts no Publish run, though, so its commit has no day tag: run Publish by hand then
(**Actions → Publish → Run workflow**), and Android follows. To recover from a release that failed, run Android by hand
too, rather than **Re-run failed jobs** on a run that isn't the latest: a re-run waits in line and can push out the run
for a newer commit.

**Web-only changes.** `Essence.UI` is the web app's too, so a change that only matters on the web, such as a fix for one
browser, still changes the app's inputs. Label its pull request **`web only`** before merging, and its changes don't count:
when every change to the app's inputs since the latest release came through such pull requests, nothing is built, and
the run's summary says which. The workflow walks `main`'s commits since the release's (first parents only, so a merge
commit stands for its pull request) and reads each one's pull request's labels when the run starts, so add the label
before Publish finishes; removing it later makes the next run count those changes again. The changes still go into the
app at its next release. These still count, labelled or not: a change to the Android workflow or its script, a change to
`src/Essence.Mobile` (with a warning), a commit GitHub can't say anything about, and in a rebase merge of several commits,
all but the last. Pushes straight to `main`,
which the ruleset blocks anyway, have no pull request to be labelled. Pull requests still get their APK to try: it checks
that the shared code still builds for the app.

**What.** It runs `tools/Essence.Release notes` as the web image does, so About shows the same changelog. Each build is a
release of its own, marked latest, with the APK `essence-<version code>.apk`, tagged `android-<version name>` at the
commit it was built from, and titled *Essence for Android* with the version name. The workflow pushes that tag with the
deploy key ([Versions](#versions)) and never moves it, nor replaces a release's APK; the `v*` tags stay the `tag` job's.
The notes list, in English and French, the changelog entries added since the previous release's commit, so a release
after skipped web-only changes lists theirs too, and a day's second release doesn't repeat the first's. To take a
release down, delete it but keep its tag, so that its number isn't given again. The first release, 2026-11-10's, has
its tag moved to `android-2026.11.10`. The Android App Bundle (the `.aab` Google Play takes) is only kept as the run's
artifact, for 30 days.

**Version name and code.** The name is the release's day with dots, and from that day's second release on, its number:
`2026.11.07`, then `2026.11.07.2`, `2026.11.07.3`… The workflow counts the day's `android-` tags, so a number whose
release failed halfway is skipped. The code is the minutes from 2026-01-01 00:00 UTC to the build, e.g. `449482`: it grows
with every build, which Android requires to update an app, and the release notes call it the build. About in the app
shows the version name. The app's update check reads the latest release and compares its APK's number with its own;
About then offers the newer one by its version name, with a link to the release's page. Pull requests that touch the app
get an APK in the run's artifacts, kept 7 days: version `0.0.0` (1), signed with a debug key, no server filled in,
*Development version* in About, no update check. Android won't install it over a release, or a release over it, since
their keys differ: uninstall first.

**Setting it up**, once:

1. Make the upload key, outside the repository. `keytool` comes with any JDK, Android Studio's included. It asks for a
   password, which the store and the key share, and for a name to put in the certificate:
   ```bash
   keytool -genkeypair -keystore essence-upload.jks -alias essence -keyalg RSA -keysize 4096 -validity 10000
   ```
2. Give it to the workflow as two Actions secrets: the keystore in base64 (on macOS, `base64 -i essence-upload.jks`), and
   its password, which `gh secret set` asks for. On github.com, that's **Settings → Secrets and variables → Actions → New
   repository secret**.
   ```bash
   base64 -w0 essence-upload.jks | gh secret set ANDROID_KEYSTORE_BASE64
   ```
   ```bash
   gh secret set ANDROID_KEYSTORE_PASSWORD
   ```
3. Set the server that the first launch suggests, as a third secret. It isn't hidden, since anyone can read it in a
   release's APK, but out of the source a fork doesn't inherit it, and as a secret it's masked in the runs' logs, which are
   public, where a variable's value would be printed.
   ```bash
   gh secret set ESSENCE_DEFAULT_SERVER --body "https://<your domain>/"
   ```
4. Keep `essence-upload.jks` and its password safe offline, say in a password manager and on a USB key, and never in the
   repository (`.gitignore` leaves out `*.jks` and `*.keystore`). **Losing it** means no update can be signed: every user
   would have to uninstall the app, losing what it saved, to install the next one. If the app ever goes on Google Play,
   this key becomes its upload key.
5. Make the label that keeps a pull request out of the app's releases ([Web-only changes](#android-app-releases)). On
   github.com, that's **Issues → Labels → New label**. Without it, every change to the app's inputs is released.
   ```bash
   gh label create "web only" --description "Changes only the web app: no Android release" --color 1D76DB
   ```

**Forks.** Without the `ANDROID_KEYSTORE_BASE64` secret, the workflow builds the APK with a debug key, keeps it as the run's
artifact and releases nothing, with a notice in the run saying so. Without the `ESSENCE_DEFAULT_SERVER` secret, the first
launch's server field is empty. An app checks for updates in the repository that built it, so a fork's follows the fork's
releases.

**Immutable releases are off** (**Settings → General**, under **Releases**). An immutable release locks its tag and its
files, which the Android releases no longer need to change: their tags never move, and no APK is replaced. Turned on,
they'd only apply to releases made after; a draft that a failed attempt left stays editable, and a deleted release's
tag can't be used again, which the workflow never does anyway.

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
| `Health:HoursStaleAfterDays` | 21 | An hours source without a success for longer makes `/health`'s `hours` check `Degraded`. |
| `Hours:Enabled` | `true` | The ingest's hours worker ([Opening hours](#opening-hours)). Off, the rows stay as they are. |
| `Hours:CachePath` | `/data/hours` | Each source's last good download. |
| `Hours:UserAgent` | `Essence-Ingest/1.0 (station hours)` | Sent to every source; a contact URL can go in the parentheses. |
| `Hours:Every` / `NightStart` / `NightEnd` | 7 days / 01:00 / 05:00 | How often each source runs (at least a day apart), and the night it's drawn in, in Québec time. |
| `Hours:Backoff` | 15 min, 1 h, 6 h | After a failed or rejected run, then after the next one, and so on; the last one repeats. |
| `Hours:MinShareOfLastGood` | 0.8 | A run matching fewer stations than this share of the last good run's is rejected. |
| `Hours:StaleAfter` | 21 days | Past this without a success, a failing source's log says its hours are stale. Keep it with `Health:HoursStaleAfterDays`. |
| `Hours:RematchEvery` | 1 h | How often the last downloads are matched again, when the stations changed. |
| `Hours:RequestTimeout` / `RetryCount` | 30 s / 0 | Per request; retries within a run. |
| `Hours:MinPause` / `MaxPause` | 2 s / 3 s | The pause before each request to a source after its first in a run (Circle K's boxes, Shell's lists and details), drawn at random between the two. |
| `Hours:Curated:Enabled` / `Costco:Enabled` / `Ultramar:Enabled` / `CircleK:Enabled` / `Shell:Enabled` / `Osm:Enabled` | `true` | Each source. Turned off, its rows are removed at the next start. |
| `Hours:Costco:Url` | Costco's locator | With `{lat}` and `{lon}` where each point goes. |
| `Hours:Ultramar:Url` | journie.ca's locator, all of Canada's stations in a page of 1,000 | One request. An answer with more pages is refused: raise its `pageSize`. |
| `Hours:CircleK:Url` | Circle K's locator, up to 2,000 stores a box | With `{north}`, `{west}`, `{south}` and `{east}` where a box's edges go. |
| `Hours:CircleK:Boxes` | Québec's west and east: 62.6, −79.8 to 44.9, −71.5 and 62.6, −71.5 to 44.9, −57.0 | Each `North`, `West`, `South` and `East`, in degrees; one request each. |
| `Hours:Shell:ListUrl` / `DetailUrl` | Shell's locator | Its list of a box, with `{north}`, `{west}`, `{south}` and `{east}`, and a station's details, with `{id}`. |
| `Hours:Shell:Box` | Québec: 62.6, −79.8 to 44.9, −57.0 | The box listed; its neighbours' stations are left out. |
| `Hours:Shell:MaxListRequests` | 100 | Lists a run may make, a cluster listed in turn until it gives stations (about 22); past it, the run fails. |
| `Hours:Osm:Endpoints` | `overpass-api.de`, then `overpass.private.coffee` (`/api/interpreter`) | The Overpass servers, asked in order: the next one when one fails. |
| `Hours:Osm:Query` | Québec's `amenity=fuel`, `out tags center` | Posted as `data`. |
| `Hours:Osm:RequestTimeout` | 4 min | Instead of `Hours:RequestTimeout`: longer than the query's own 180 s. |
| `Routing:BaseUrl` | `http://routing:5000/` | The routing server (OSRM), ending with `/`. 3 s per attempt and one retry. |
| `Routing:StatusUrl` | `http://routing:9090/` | The routing container's status server, ending with `/`. |
| `Geocoding:BaseUrl` | `http://geocoding:2322/` | The address search (Photon), ending with `/`. 1.5 s, no retry. |
| `Geocoding:StatusUrl` | `http://geocoding:9090/` | The geocoding container's status server, ending with `/`. |
| `Clock:Now` | (none) | Development only: the API's clock starts at this moment (ISO 8601 with its offset) and moves on with the real one, to see which stations Best near you and Best on the way leave out at another time of day. Ignored in any other environment. |
| `Osm:ClientId` | (none) | The client id of the deployment's OpenStreetMap OAuth 2 application ([OpenStreetMap edits](#openstreetmap-edits)), served by `/client-config`. Empty: the apps don't offer to send corrections to OpenStreetMap. |
| `Osm:AuthUrl` / `ApiUrl` | `https://www.openstreetmap.org` / `https://api.openstreetmap.org` | Where that application signs in and where edits go: `https://master.apis.dev.openstreetmap.org` for both, to test against OpenStreetMap's development server with an application registered there. |
| `RateLimiting:PermitLimit` / `WindowSeconds` | 120 / 60 | Requests per client IP and window, the address search's aside. |
| `RateLimiting:Search:PermitLimit` / `WindowSeconds` | 60 / 60 | `/places/` requests per client IP and window. |

The routing and geocoding containers take environment variables: `REFRESH_DAYS` (30; 0 never refreshes), and
`ROUTING_PBF_URL` (Geofabrik's `quebec-latest.osm.pbf`) or `GEOCODING_DUMP_URL` (Photon's dump of Canada), each with an
`.md5` beside it; a copy of the extract must also send its `Last-Modified`, which tells whether it changed.
