# Plan: Essence, a Québec fuel-price ingester + API (.NET 10, PostgreSQL, Docker)

> **Handoff status (2026-10-12):** planning is finished and every decision below was confirmed by the owner (Etienne Scott) through Q&A.
> No code is written yet. The repo has `git init` on branch `main`, with no commits and no remote.
> **To resume on another machine:** install the prerequisites (bottom of this file), clone or copy the repo, put the sample file at
> `tests/Essence.Ingest.Tests/Fixtures/stations.sample.geojson` (it's plain JSON despite the original `.gz` name),
> place this plan in the repo (suggested: `docs/PLAN.md`), then tell Claude: *"Implement docs/PLAN.md"*, starting at "Build order" step 1.

## Context
The repo `C:\dev\essence` is empty (branch `main`). The Régie de l'énergie publishes `https://regieessencequebec.ca/stations.geojson.gz`:
a GeoJSON list of about 2,460 Québec fuel stations, each with its current prices, overwritten every 5 minutes at the same URL.
Goal: continuously ingest it into PostgreSQL, keeping the station list and a full price-change history,
and expose it through a read API that a future web app will use. Everything runs with docker-compose.

## What the data and endpoint look like (measured)
- **File**: `{ type, metadata{generated_at (UTC), excel_url, total_stations, excel_size_bytes}, features[] }`. Each feature is a
  `Point [lon,lat]` with `Name, brand, Status, Address, PostalCode, Region, Prices[{GasType, Price:"199.4¢"|null, IsAvailable}]`.
  GasType ∈ Régulier/Super/Diesel, 2–3 per station. **No station ID**. Address+PostalCode is unique; Name is not.
  Oddities: "Hub Régie" at 0,0; 1 empty Name; 22 empty brands; brand "Aucun" ×19. `excel_url` is ignored.
- **Endpoint**: Azure Blob behind Azure Front Door, `max-age=60`, always `Content-Encoding: gzip` (~127 KB → ~2 MB).
  ETag/Last-Modified are supported (304 responses). A query string doesn't bypass the cache.
- **Timing (30-min watch)**: generated at **:00:01, :05:01, … UTC**. It shows up through the CDN **43–92 s** later.
  **The CDN flip-flops**: after serving the new file, it often serves the previous one again. One request hung about 4.5 min.

## Decisions
| Topic | Choice |
|---|---|
| Runtime | .NET 10 LTS. Separate **Ingest worker** and **API** apps, plus a one-shot **Migrator**. `Essence.Web` is a placeholder (README only) |
| DB | PostgreSQL 18 (official image) + `cube`/`earthdistance` extensions. EF Core + Npgsql, snake_case naming |
| Station identity | `natural_key` = normalized Address + PostalCode (trim, collapse whitespace, lowercase, accents removed; postal code uppercase without spaces) |
| Station edits | Overwrite the row + append to `station_changes` |
| Missing stations | `is_active=false`, close their prices. If one comes back: reactivate and log the change |
| Prices | `numeric(5,1)` cents/L, `null` when unavailable. Change-only `price_history` + `current_prices` table |
| Lookups | `fuel_types` table (rows added automatically). Brand/region/status stored as text |
| Data quality | Import everything. Coordinates of 0,0 → null lat/lon. Empty or "Aucun" brand → null. An unparsable price skips that one row and is counted as a warning |
| Import | In-memory diff, a single transaction, writing only the changes |
| Raw archive | Each new file saved gzipped as received to a volume, kept 30 days |
| API | Open, read-only, minimal APIs, CORS, rate limiting, OpenAPI + Scalar, read-only DB role |
| Logging | Built-in logging with the JSON console formatter. Per-import stats go in the `imports` table |
| Tests | Unit tests only (xUnit): parse, diff, scheduler, fetch decisions |

**Settled during Q&A, don't reopen:** PostGIS was rejected in favour of plain Postgres + earthdistance. TimescaleDB is unnecessary because only changes are stored.
A fixed 5-min poll was rejected in favour of a schedule aligned to generated_at with a learned delay (the owner noticed the official site polls every 1 min and that Last-Modified ≠ availability time).
Station-level full versioning (SCD2) was rejected in favour of a change log. Auth, PostGIS and integration tests are possible later additions, not v1.

## Solution layout
```
Essence.slnx, Directory.Build.props (net10.0, nullable, warnings-as-errors), Directory.Packages.props (central versions)
.gitignore, .dockerignore, .editorconfig, .config/dotnet-tools.json (dotnet-ef), README.md
docker-compose.yml, .env.example
src/Essence.Data/       EF DbContext, entities, migrations, StationKey normalizer (shared by all apps)
src/Essence.Ingest/     Worker SDK: FeedClient, FeedParser, FeedDiffer, ImportWriter, PollScheduler, RawArchive, IngestWorker + Dockerfile
src/Essence.Api/        ASP.NET Core minimal APIs + Dockerfile
src/Essence.Migrator/   console: applies migrations, creates/grants roles + Dockerfile
src/Essence.Web/        README.md only (future front-end calling Essence.Api)
tests/Essence.Ingest.Tests/  xUnit + Fixtures/stations.sample.geojson (copy of the downloaded sample)
```

## Schema (Essence.Data)
- `fuel_types(id smallint PK, code text unique)`
- `stations(id, natural_key unique, name?, brand?, status, address, postal_code, region, latitude?, longitude?, is_active, first_seen_at, last_seen_at, updated_at)`.
  GiST index on `ll_to_earth(latitude, longitude)` where lat is not null (raw SQL in the migration, which also runs `CREATE EXTENSION cube, earthdistance`). Indexes on region and brand.
- `station_changes(id, station_id, import_id, field, old_value?, new_value?, changed_at)`. Fields include `is_active` and `created`.
- `price_history(id, station_id, fuel_type_id, price_cents numeric(5,1)?, is_available, valid_from, valid_to?, import_id)`. Index on `(station_id, fuel_type_id, valid_from desc)`.
- `current_prices(station_id, fuel_type_id PK, price_cents?, is_available, since, price_history_id)`. Index on `(fuel_type_id, price_cents)`.
- `imports(id, fetched_at, generated_at unique, etag, sha256, archive_path, delay_seconds, stations_total/added/updated/deactivated, prices_changed, warnings, status, error?)`
- All timestamps are `timestamptz` in UTC. **The effective time of a change is the file's `generated_at`**, not the time it was fetched.
  Unchanged prices are never touched, so there are no writes per poll for rows that didn't change.

## Ingestion algorithm (Essence.Ingest)
1. **PollScheduler** (pure logic with `TimeProvider`). Startup state (last `generated_at`, last 50 `delay_seconds`) is loaded from `imports`.
   - expected = last generated_at + 5 min (rolls forward to the next 5-min slot if slots were missed).
   - window opens at expected + learnedDelay. learnedDelay = 20th percentile of recent delays, clamped to 20–120 s, initially 40 s.
   - Inside the window: poll every 10 s. After 3 min with no new file: poll every 60 s until the next window opens. After an import: sleep until the next window.
   - ±2 s jitter. With no history at all: poll immediately. All values come from the `Feed` config section.
2. **FeedClient**: typed HttpClient with no automatic decompression (raw gzip bytes are archived) and `Accept-Encoding: gzip`.
   `If-None-Match` carries the ETag of the newest imported version. Timeout 30 s, plus `Microsoft.Extensions.Http.Resilience` (2 retries).
   Outcomes: 304 → nothing new. 200 → decompress (gzip or identity), SHA-256, parse.
   **Discarded if `generated_at` ≤ the last imported one** (CDN flip-flop) or if the SHA matches the last import. Neither case updates the ETag.
3. **FeedParser**: System.Text.Json source-generated DTOs. Prices are parsed with InvariantCulture after stripping `¢`.
4. **RawArchive**: writes `/data/raw/yyyy/MM/dd/stations-{generated_at:yyyyMMddTHHmmssZ}.geojson.gz` *before* importing, and deletes files past retention once a day.
5. **FeedDiffer** (pure, no database): (current stations + current prices snapshot, parsed feed) → ChangeSet:
   new stations, field changes, reactivations, deactivations (in DB but not in the file), price changes (price or availability differs),
   closed prices (station dropped a fuel type, or the station went missing). Duplicate keys in one file: keep the first, log a warning.
6. **ImportWriter**: one transaction with `pg_advisory_xact_lock`, which guards against a second worker. It applies the ChangeSet
   (closes `valid_to`, inserts history, upserts `current_prices`, writes `station_changes`), inserts an `imports` row, and commits.
   On failure: roll back and log a failed `imports` row in a separate transaction, then retry on the next poll. After 3 failures with the same SHA, skip that file (it's archived).

## API (Essence.Api)
`GET /stations` (region, brand, active, q, paging) · `GET /stations/{id}` (+ current prices) ·
`GET /stations/nearby?lat&lon&radiusKm=5&fuel&limit` (`earth_box` prefilter + `earth_distance`, sorted by distance, via `Database.SqlQuery<T>`) ·
`GET /prices/current?fuel&region&brand&sort=price&limit` · `GET /stations/{id}/prices?fuel&from&to` ·
`GET /imports?limit` · `GET /health` (DB reachable + last successful import < 15 min).
OpenAPI docs come from the built-in `Microsoft.AspNetCore.OpenApi`, with a Scalar UI at `/scalar`. CORS origins come from config. A fixed-window rate limit applies per IP. Errors use ProblemDetails.

## Docker
- Multi-stage Dockerfiles (`sdk:10.0` → `runtime:10.0` / `aspnet:10.0`), running as the image's non-root `app` user.
- Compose: `postgres` (healthcheck, `pgdata` volume) → `migrate` (runs once, waits for a healthy postgres, creates the `essence_writer`/`essence_reader` roles)
  → `ingest` (`restart: unless-stopped`, `rawdata` volume at `/data/raw`) and `api` (port 8080), both waiting for `migrate` to complete successfully.
  Secrets come from `.env` (gitignored). `.env.example` is committed.

## Build order
1. Scaffold: solution, props files, gitignore/editorconfig, projects, Web README, dotnet-ef tool manifest.
2. Essence.Data: entities, DbContext, StationKey, initial migration.
3. Parser, price parser, FeedDiffer, plus their unit tests.
4. FeedClient, PollScheduler, RawArchive, ImportWriter, IngestWorker, plus tests.
5. Migrator → API → Dockerfiles, compose, README.
Changes stay uncommitted for your review. I'll commit when you ask.

## Prerequisites (you install)
`winget install Microsoft.DotNet.SDK.10` and `winget install Docker.DockerDesktop` (needs WSL2, and a reboot may be required).

## Verification
- `dotnet build` and `dotnet test`. Unit tests cover:
  - parsing the fixture (2463 stations, 7127 prices, 249 unavailable)
  - price parsing, and key normalization equivalence
  - each diff case (new, unchanged, price change, availability change, brand change, missing → deactivate, reappear, dropped fuel, duplicate key)
  - scheduler windows, fallback and slot realignment with `FakeTimeProvider`
  - a FeedClient sequence (304; then new → old → new) imports exactly once, using a fake HttpMessageHandler
- `docker compose up --build`:
  - `migrate` exits 0, and `ingest` logs a first import (2463 stations)
  - over about 15 min, the logs show one import per publish with delay of about 40–90 s, plus "stale version ignored" entries
- API checks: `curl localhost:8080/health`, `/stations/nearby?lat=45.5017&lon=-73.5673&radiusKm=3&fuel=Diesel`, `/prices/current?fuel=Régulier&limit=5`, `/imports`. `/scalar` loads.
- psql: `price_history` grows only by actual changes, `current_prices` has about 7,100 rows, and archive files show up in the `rawdata` volume.
