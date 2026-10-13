# Essence

Continuously ingests Québec fuel prices published by the Régie de l'énergie
(`https://regieessencequebec.ca/stations.geojson.gz`, about 2,460 stations, regenerated every 5 minutes)
into PostgreSQL, keeping the station list and a full price-change history, and serves them through a read-only HTTP API.

| Project | What it does |
|---|---|
| `src/Essence.Data` | EF Core model, migrations, `StationKey` normalizer. Shared by every app. |
| `src/Essence.Ingest` | Worker: polls the feed, archives each new file, diffs it against the database and writes only the changes. |
| `src/Essence.Api` | ASP.NET Core minimal API (read-only DB role), OpenAPI + Scalar UI. |
| `src/Essence.Migrator` | One-shot: applies migrations, creates the `essence_writer` / `essence_reader` roles and grants them. |
| `src/Essence.Web` | Placeholder for the future front-end. |
| `tests/Essence.Ingest.Tests` | xUnit unit tests: parsing, diffing, scheduling, fetch decisions, archive. |

The full design and its rationale are in [docs/PLAN.md](docs/PLAN.md).

## Run it

Requires Docker Desktop.

```bash
cp .env.example .env    # then edit the passwords
docker compose up --build
```

Compose starts `postgres`, then `migrate` (exits 0 once the schema and roles are in place), then `ingest` and `api`.
The API listens on <http://localhost:8080>; the interactive docs are at <http://localhost:8080/scalar>.

```bash
curl localhost:8080/health
curl "localhost:8080/stations/nearby?lat=45.5017&lon=-73.5673&radiusKm=3&fuel=Diesel"
curl "localhost:8080/prices/current?fuel=Régulier&limit=5"
curl localhost:8080/imports
```

Look at the data with `docker compose exec postgres psql -U essence_admin essence`, and at the raw archive with
`docker compose exec ingest ls -R /data/raw`.

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

To run an app outside Docker, give it a connection string, e.g.
`ConnectionStrings__Essence="Host=localhost;Database=essence;Username=essence_writer;Password=…"`.
In the Development environment, logs are plain text and the ingest archive goes to `./data/raw`.

## How ingestion works

- **Schedule.** Files are generated at :00:01, :05:01, … UTC and reach the CDN 40–90 s later. The worker expects the next file
  at the last `generated_at` + 5 min and opens a polling window after a learned delay (20th percentile of the last 50 delays,
  clamped to 20–120 s, 40 s at first). In the window it polls every 10 s; after 3 min without a new file it polls every 60 s until
  the next window. After an import it sleeps until the next window. Every poll time gets ±2 s of jitter.
- **Fetch.** Conditional GET with the ETag of the newest imported file. A response is discarded if its `generated_at` is not newer
  than the last import (the CDN flip-flops between edges) or if its SHA-256 matches the last import.
- **Archive.** Each new file is saved, gzipped as received, to `/data/raw/yyyy/MM/dd/stations-{generated_at}.geojson.gz`
  before it is imported. Files older than 30 days are deleted once a day.
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

## API

| Endpoint | |
|---|---|
| `GET /stations?region&brand&active&q&page&pageSize` | Stations, paged. `q` searches the name, address and postal code. |
| `GET /stations/{id}` | One station with its current prices. |
| `GET /stations/nearby?lat&lon&radiusKm=5&fuel&limit=20` | Active stations within a radius, closest first. |
| `GET /stations/{id}/prices?fuel&from&to` | Price history overlapping a range (default: the last 30 days). |
| `GET /prices/current?fuel&region&brand&sort=price&includeUnavailable&limit=50` | Current prices, cheapest first. |
| `GET /imports?limit=20` | Recent imports with their statistics. |
| `GET /health` | 200 when the database answers and the last successful import is under 15 min old, else 503. |

Fuel names accept or omit accents (`Regulier` = `Régulier`). Errors are ProblemDetails. Requests are rate limited per client IP
(`RateLimiting:PermitLimit` per `RateLimiting:WindowSeconds`, 120/60 s by default). Allowed CORS origins come from
`Cors:AllowedOrigins` (`CORS_ORIGIN` in `.env`).

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
| `Archive:Path` / `RetentionDays` | `/data/raw` / 30 | |
| `Health:MaxImportAgeMinutes` | 15 | API `/health` threshold. |
