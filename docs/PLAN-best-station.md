# Plan: Best near you and Best on the way

> **Status (2026-10-24):** designed with the owner through mockups and Q&A; nothing is built yet. Every decision below was
> confirmed by the owner. Work in phase order: each phase ships on its own and is usable without the next.
> To resume: tell Claude *"Implement docs/PLAN-best-station.md, phase N"*.

## Context
Today the map shows prices and lets you open a station, star it or compare it. This adds a recommendation: **which station
to go to**, counting the gas burned getting there, first around you and then on the way to a destination. It's a new pane,
separate from the favorites and the comparison, and it's meant for anyone using the site, not only the owner.

- **Best near you.** One station first ("Costco, saves $2.24"), then tiles for the closest, the best value and the
  cheapest, then a few more worth the drive. Opened from a toolbar button (a fuel pump) that takes the place of Locate Me,
  and on phones from a chip under the toolbar once the location is known.
- **Best on the way.** A destination (typed with autocomplete, a saved place such as Home or Work, or a press and hold on
  the map) turns the pane into trip mode: the route on the map, the stations along it ranked by detour, "stop before
  km 150" from the range the driver types, and "Go via · Google Maps" with the station as a stop.
- **My cars.** Two or more profiles per device (name, fuel, usual fill, consumption), so savings are worked out for the
  car being driven.

## Decisions
| Topic | Choice |
|---|---|
| Ranking | cost ($) = price (¢/L) ÷ 100 × (fill L + extra km × L/100 km ÷ 100). Near you, the extra km are there and back; on the way, the detour. **Saves** = cost of the reference − cost of the station |
| Reference | Near you: the closest station with a price. On the way: the cheapest reachable station with no detour (defined in phase 2) |
| Time | Not priced. A **longest detour** limit (10 min by default) keeps a station from being picked; it's still listed. On a trip it's the detour's minutes; near you, how much longer the station's round trip is than the closest's |
| Search area | Best near you looks within 10 km, widened to 25 km when fewer than 5 stations have a price, then to the closest priced station at any distance |
| Small differences | Under $0.25 shows as "About the same", and then the pick is the reference itself, not a farther station |
| Which stations | Active, status "En opération", with an available price for the fuel. Costco only when the user says they're a member |
| Prices | No "old price" flag: the Régie requires stations to update within 5 minutes |
| Gas left | Typed from the dashboard (km of range), with the time it was typed; 30 km kept spare. Trips under 100 km don't ask. It isn't asked again as it ages. OBD2 reading is optional and comes last (phase 4) |
| Long trips | The first stop only. Prices that change during a long trip are ignored for now |
| Distances | By road, from a self-hosted **OSRM** next to the API (phase 2). Phase 1 estimates them (straight line × 1.3, shown as "≈") |
| Routing data | Built **on the NAS** by the routing image itself: once at install, then by a monthly cron job, while the server keeps answering from the previous data |
| Places | Self-hosted **Photon** address search with autocomplete (phase 3), plus press and hold on the map (phase 2). Saved places (Home, Work, others) on the device |
| Area | Québec only. Routes and searches outside it are refused; nothing past the border |
| Navigation | Hand-off to Google Maps, the station as a waypoint |
| Profiles | 2+ car profiles per device in `IPreferenceStore`, no accounts. Each car has its own fuel, usual fill, consumption, Costco membership, spare km and longest detour. Ranking uses the toolbar's fuel; picking a car switches the toolbar to the car's fuel, and changing the toolbar fuel while a car is active updates that car |
| Toolbar | Locate Me goes; the pump button opens Best near you and refreshes the location first. Favorites and Comparison stay |
| Where the work runs | Ranking, corridor and routing calls in **Essence.Api**; the UI only displays. Pure logic in a new dependency-free library with its own tests |

**Settled during Q&A, don't reopen:** pricing the driver's time was rejected in favour of a longest-detour limit. PostGIS
stays out (see [PLAN.md](PLAN.md)): the corridor is computed in memory over ~2,460 stations. Hosted routing and geocoding
APIs (keys, quotas) were rejected in favour of self-hosting, and building the routing data in CI in favour of building it
on the NAS. Flagging prices that haven't changed in days was rejected. Accounts were rejected: everything personal stays on
the device. Several stops per trip, re-planning while driving and asking for the range again were left to a later
"follow me" feature.

## Architecture
```
browser ──/api/──> nginx (web) ──> Essence.Api ──> PostgreSQL (read-only role)
                                     │  ├─ StationSnapshot: every active station and current price, cached per import
                                     │  ├─ Essence.Ranking: costs, ranking, corridor, range (pure, unit-tested)
                                     │  ├─ OsrmClient ───> routing:5000   (phase 2, internal only)
                                     │  └─ PhotonClient ─> photon:2322    (phase 3, internal only)
```
- **New library `src/Essence.Ranking`** (no dependencies, like Essence.Contracts; public types, since no project uses
  `InternalsVisibleTo`) and **`tests/Essence.Ranking.Tests`** (xunit.v3 like the others). Add both to `Essence.slnx`, so
  CI's `dotnet test` runs them, and the library to both COPY blocks of `src/Essence.Api/Dockerfile` (`select-images.sh`
  reads an image's inputs from those lines). It holds the cost and savings maths, the reference and pick rules,
  polyline decoding and simplification, distance from a point to a polyline and km along it, the range check, the
  candidate selection, and parsing of OSRM and Photon responses. Only the API references it: the UI gets the route as a
  ready-to-draw coordinate array, so the web image doesn't change.
- **`StationSnapshot`** (API singleton): one projected EF query of active stations with coordinates and their current
  prices, keyed on the latest import (make `DataVersion.LatestAsync` internal and reuse it), behind a `SemaphoreSlim` so
  a new import loads once. Filtering (status, availability, Costco) happens on the snapshot. `/stations/nearby` can't be
  reused: it returns the 100 closest, which in Montréal covers only a few km, and it checks neither status nor
  availability. The Costco test uses the brand slug: move `BrandCatalog.Slugify` into Essence.Contracts so the API and
  the UI share it.
- **Contracts** (positional `sealed record`s, shared with the UI; each needs a `[JsonSerializable]` in the UI's
  `EssenceJsonContext`). Money is `decimal`, each cost rounded to the cent (`MidpointRounding.AwayFromZero`), and Saves is
  the difference of the rounded costs, so the hero's figure matches the tiles. Sketch, to refine while building:
  - `BestNearby(RankedStation? Pick, long? ReferenceId, long? ClosestId, long? CheapestId, IReadOnlyList<RankedStation> Stations, double RadiusKm, bool ByRoad)`
  - `RankedStation(Id, Name, Brand, Address, Latitude, Longitude, PriceCents, DistanceKm, DurationMin?, CostDollars, SavesDollars, MembersOnly)`
  - `TripPlan(double[][] Path, double DistanceKm, double DurationMin, double? StopBeforeKm, bool LowFuel, IReadOnlyList<TripStation> Stations, long? PickId, long? ReferenceId, long? BiggestSavingId)` (`Path` is [lon, lat] pairs, simplified)
  - `TripStation(…RankedStation fields…, KmAlong, DetourKm, DetourMin, Reachable, OverDetourLimit)`
  - `PlaceSuggestion(Label, Detail, Latitude, Longitude, Kind)`
- **Every new endpoint is a GET** (CORS allows GET only), rate limited, documented with `[Description]`/`WithSummary`, and
  returns ProblemDetails: 400 for bad input, 422 when there's no road or route (outside Québec included), 503 when OSRM
  or Photon is down. `EssenceApiClient` today turns every failure into `ApiError.Failed`: add `Invalid`, `NoRoute` and
  `Unavailable`, with tests, so the UI can tell them apart.
- **Device storage** follows `FavoriteStations`: a scoped service over `IPreferenceStore` with `LoadAsync`, lenient reads
  (bad JSON = empty) and a `Changed` event. New keys: `essence.cars` (each car with its fuel, fill, consumption, Costco
  membership, spare km, longest detour and its last typed range with the time), `essence.car` (the active one) and
  `essence.places`. An open trip is kept in sessionStorage under its own key, read on every load (not only after a
  release reload), so it survives Android discarding the tab during Google Maps navigation.
- **Privacy.** Coordinates and typed addresses must not reach logs:
  - `Essence.Api/appsettings.json`: `System.Net.Http.HttpClient` and `Polly` at Warning, as Ingest already does.
  - nginx (`src/Essence.Web/nginx/default.conf`): a `map $request_uri $essence_log { ~^/api/(stations/(best|nearby)|trips/|places/) 0; default 1; }`
    and `access_log … if=$essence_log;` in `location ^~ /api/` (nested locations would lose its `proxy_pass` and
    rewrite). `/stations/nearby` is included because after a locate the map centre is the user's position.
  - `osrm-routed -l WARNING` (it logs every request path at INFO); Photon's request logging checked and turned down.
  - The destination stays out of the URL. README: the installer's own HTTPS proxy logs query strings too.

## Phase 1: Best near you (no new infrastructure)
**Ships:** the pump button, the phone chip, the Best near you pane, My cars and the Costco setting. Distances are
straight line × 1.3, shown with "≈", until phase 2.

1. **Essence.Ranking** + tests.
   - Fill cost; the near-you ranking: reference = closest priced; pick = lowest cost; cheapest = lowest price; ties by
     cost, then distance, then price, then id; "About the same" under $0.25, and then the pick is the reference.
   - Extra km near you = 2 × one-way distance; the one-way estimate is straight line × 1.3 (a named constant).
   - Longest detour near you: a station is picked only if its round trip takes at most that many minutes more than the
     closest's. Until phase 2 the minutes are estimated from the estimated km at 40 km/h (a named constant).
   - Safety rule while distances are estimated: a station other than the closest becomes the pick only if it still saves
     at least $0.25 with a road factor of 2.0. Crossing the St. Lawrence or the Rivière des Prairies can make roads 2–4×
     the straight line, which would otherwise reverse a "saves $2" pick.
   - Filters: status "En opération", `IsAvailable && PriceCents != null`, Costco by brand slug `costco`.
   - Test against the mockup numbers: Costco 155.9 at 9.0 km = $64.75, Ultramar 166.9 at 0.8 km = $66.99, so Costco
     saves $2.24 (40 L, 8.5 L/100 km).
2. **API:** `StationSnapshot`; `GET /stations/best?lat&lon&fuel&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&radiusKm=10` in
   `StationEndpoints`, with `.WithDataVersion()`. Radius up to 25 km; with fewer than 5 priced stations it widens to
   25 km (`RadiusKm` says which), and with none, it returns the closest priced station at any distance.
3. **UI services:**
   - `CarProfiles`: list + active car, each with its own settings. Until one is set up, a default "My car" (40 L,
     8.5 L/100 km, the toolbar's current fuel, not a Costco member, 30 km spare, 10 min longest detour); the footer
     says it's a typical car and links to My cars.
   - `BestNearYou`: a scoped service holding the latest result, so it survives a station's details opening over the
     pane. Refetches when the location, fuel or car change. It runs **its own refresh timer**
     (`RefreshSchedule.Next` on its own `DataAsOf`, paused while hidden): MapPage's timer stops below zoom 9.
   - `EssenceApiClient.GetBestAsync` and the new `ApiError` values. `Formatting.Money` from the culture's number format
     with the symbol forced to `$` ("$64.75" in English, "64,75 $" in French); plain `"C"` would show £ or € on some
     browsers.
   - When the location's accuracy is worse than 1 km (desktops), say it's approximate and offer the map centre.
4. **Toolbar and chip** (`MapPage.razor`): the pump replaces Locate Me (path added to `Components/Icons.cs`). It calls
   `LocateAsync(moveMap: false, …)` and then opens `?view=best`; the pane fits the map to the user, the pick and the
   closest. Keep the load-time locate, the Montréal fallback and the location notices; remove the `LocateMe` strings.
   The chip goes in `.status-slot` after the StatusChip, but outside its `role=status` element; it shows under 768 px
   when the user is located, no pane is open and a pick exists. `ListOpen` must include the new views.
5. **`BestPanel.razor`** in `Pane`: hero card (logo, name, address, distance, price, "Saves $X", why, Go · Google Maps,
   Details), the Closest / Best value / Cheapest tiles, "Also worth it" (2–3 rows), and the footer ("40 L at 8.5 L/100 km
   (Mazda3) · Change"). The car chip in the header opens My cars. Empty and error states: nothing priced within 25 km
   (the closest priced station at any distance), location denied (offer the map centre), API failure with Retry.
6. **`MyCarsPanel.razor`** at `?view=cars`, **pushed** on top of `?view=best` so Back returns to it. `ShowListAsync`
   replaces the entry when a list is open, so add a `CarsPushed` flag next to `ListPushed`, in `ReloadState`,
   `SavedView` (`MapInterop.cs`) and the `takeSavedView` whitelist (`essenceMap.js`). Contents: car switcher, + Add /
   delete, and for the car: name, fuel, usual fill, consumption, Costco member, keep spare, longest detour.
7. **Map** (`essenceMap.js`, `MapInterop`):
   - The ranked stations are merged into the marker models in `RenderMarkersAsync`. A station 9 km away is usually not
     in `StationViewport`, and `setMarkers` deletes any marker it isn't given.
   - `setEmphasis(pickId, closestId, dimOthers)` toggling `is-best`, `is-closest`, `is-dimmed` like `setSelected`, also
     applied in `renderMarker`; CSS for the "BEST VALUE" flag and the closest ring, and the compact-zoom selectors
     extended so the pick stays a pill. "Best value" and "closest" also go in the markers' `aria-label` (`ToMarker`).
   - `fitBounds(w, s, e, n, besidePane)` with padding from `uncoveredArea()`, counted in `pendingMoves`.
8. **Google Maps URL builder** in Essence.UI (tested), used by the hero card and by `StationDetails.razor`, which builds
   the URL inline today.
9. **Strings** in `Strings.resx` and `Strings.fr.resx`; **changelog** `best-near-you.json` (say that the fuel pump took
   Locate Me's place) and `car-profiles.json`; **README**: the project table (Essence.Ranking and its tests), the API
   table, the Essence.Web table.

**Done when:** `/stations/best` near Montréal returns a pick, closest and cheapest consistent with the snapshot; Costco
shows up only with `costco=true`; the pick is never across the river from the user unless it still saves with the 2.0
factor; a station whose round trip is more than 10 min longer than the closest's is listed but not picked; the pane matches the mockup at 390 px and on desktop, in both languages; switching car or fuel re-ranks; the pick
has a marker even when it's outside the viewport fetch; the chip disappears while a pane is open; Back from My cars
returns to the pane; a release reload reopens the pane.

## Phase 2: Road distances and Best on the way
**Ships:** distances and minutes by road in Best near you; trip mode with a dropped pin or a saved place as destination.

1. **Routing image** `src/Essence.Routing/` (a Dockerfile, an entrypoint script and a profile; no .NET project). It holds
   the programs only; the map data lives on a NAS dataset and is built there.
   - Base: `ghcr.io/project-osrm/osrm-backend:v26.10.0-debian` pinned by digest, plus `curl ca-certificates` (the OSRM
     image has none), the entrypoint and `essence-car.lua`; runs as user 1654.
   - `essence-car.lua` wraps `car.lua` and marks `motorway` and `motorway_link` as not start points, so stations on
     service roads beside an autoroute snap to their own road instead of the autoroute (otherwise their detour reads 0).
     The trip's origin and destination still snap anywhere: pass the `route` call's waypoint `hints` to the table calls.
     Test it on a station on an A-40 service road.
   - `serve` (the default command): `osrm-routed --algorithm mld --max-table-size 200 -l WARNING
     /data/current/quebec.osrm`. It refuses to start, with a clear log line, when `/data/current` is missing or was built
     by another OSRM version (files built by one version may not load in another). Healthcheck through bash's
     `/dev/tcp`.
   - `build`: `curl -L` the Geofabrik `north-america/canada/quebec-latest.osm.pbf` into `/data/next`, `md5sum -c`
     against its `.md5`, `osrm-extract -p /opt/essence-car.lua`, `osrm-partition`, `osrm-customize`, write the OSRM
     version, delete the `.pbf`, then swap: `current` becomes `previous` and `next` becomes `current`. The server keeps
     answering from the old files until it's restarted.
2. **CI:** nothing special. `select-images.sh` picks up `src/Essence.Routing/Dockerfile` as `essence-routing`, a small
   image built and promoted with the others. After the first promote on main, make the new package public (package
   settings → visibility): the autodeploy reads digests anonymously and stops every rollout on a 401/403/404.
3. **Deploy:**
   - A dataset `/mnt/POOL/apps/essence/data/routing` (its own dataset, owned by 1654, excluded from snapshots: it can be
     rebuilt). Disk: the 1.2 GB download plus a few GB of processed files, twice during a refresh.
   - First install: run the build once by hand (`docker run --rm -v …/data/routing:/data
     ghcr.io/etiennex9/essence-routing:latest build`; about 15–30 minutes, 4–8 GB of RAM at the peak, to be measured),
     then add a `routing` service to `deploy/truenas.yaml`: no ports, `mem_limit` = the measured data size + 1 GB (no
     `--mmap` on ZFS, which would cache the data twice), the healthcheck, and no `depends_on` from the api, which must run
     without routing. Building first matters: an app that isn't RUNNING within 10 minutes fails the autodeploy.
   - Monthly refresh: `deploy/truenas-routing-refresh.sh`, a root cron job set up like the autodeploy one, runs the build
     container and then restarts the `routing` container. While it builds, the NAS needs RAM for both (serving about
     2 GB plus the build peak). Pick a night hour.
   - Bumping the pinned OSRM version is deliberate: the new image refuses the old data, so run the refresh right after
     the rollout (the README says so next to the version).
   - `docker-compose.yml`: the same `routing` service under `profiles: [trip]`, so a plain `docker compose up` doesn't
     need it, plus the same one-time build command for development.
   - README: the image list ("all five"), the dataset, the first build, the refresh cron job, RAM and disk.
4. **API:** `OsrmClient` (typed `HttpClient` + `AddStandardResilienceHandler`: about 3 s per attempt, 1 retry; add the
   `Microsoft.Extensions.Http.Resilience` PackageReference to `Essence.Api.csproj`; options `Routing:BaseUrl`, default
   `http://routing:5000/`), a health check registered as Degraded.
   - OSRM answers HTTP 400 for every error: read the body's `code`. NoRoute / NoSegment → 422; TooBig and Invalid* →
     500, logged as a bug; 5xx, timeout or refused → 503.
   - A trip's origin or destination snapped more than 1 km away (`waypoints[].distance`) → 422 "No road near this
     point". This also refuses Ottawa, Edmundston or Vermont, which a Québec bounding box lets through and OSRM would
     quietly snap to the nearest Québec road. A station whose table cell is `null` or that snaps more than 300 m away is
     dropped.
   - `/stations/best` by road: one table call U → S and one S → U (`annotations=distance,duration`, explicit `sources`
     and `destinations`, at most 200 candidates: the 10 nearest, then every station cheaper than the closest, nearest
     first). Extra km = d(U,S) + d(S,U); closest = smallest d(U,S). If OSRM is down it falls back to the phase-1
     estimate with `ByRoad = false`; road and estimated figures are never mixed in one ranking.
5. **`GET /trips/best?fromLat&fromLon&toLat&toLon&fuel&fillL&per100Km&costco&maxDetourMin=10&rangeKm&spareKm=30`** in
   `Endpoints/TripEndpoints.cs`, with `.WithDataVersion()`:
   1. `route` O → D with `overview=full`. Cumulative km on the full geometry give KmAlong and StopBeforeKm; a
      Douglas–Peucker simplification at 25 m is used for the corridor test and returned as `Path`. If OSRM's
      distance and the polyline's length differ, KmAlong is scaled to OSRM's.
   2. Corridor in memory: the route's bounding box plus the half-width, then stations within the half-width of the line,
      half-width = clamp(maxDetourMin × 0.5, 1, 8) km (a station x km off the line costs at least 2x km of driving).
   3. Candidates, at most 100: when the range applies, drop those with KmAlong > range − spare + half-width; then, in
      10 km bins along the route, keep the 2 closest to the line and the 3 cheapest; fill up to 100 by price.
   4. Table call 1: `O;D;S1…Sn` with `sources=0&destinations=1;2;…;n+1`, giving d(O,D) and d(O,S) under the same
      snapping. Call 2: `sources=2…n+1&destinations=1` for d(S,D). Detour = max(0, d(O,S) + d(S,D) − d(O,D)), in km and
      minutes.
   5. Range: Reachable = d(O,S) ≤ range − spare. StopBeforeKm = range − spare when the trip is longer; "no stop needed"
      otherwise. If range − spare ≤ 0, or nothing is reachable that way: `LowFuel`, StopBeforeKm = 0, Reachable by
      d(O,S) ≤ range alone, and if still nothing, the station with the smallest d(O,S). Trips under 100 km don't ask for
      the range.
   6. Rank, among Reachable stations only:
      - reference = cheapest with **no detour** (DetourKm ≤ 1.0 and DetourMin ≤ 2.0, named constants); if none, the one
        with the smallest DetourMin, labelled "Smallest detour". Its cost includes its own detour km.
      - pick = lowest cost among those within the longest detour; if it saves under $0.25, the pick is the reference.
      - biggest saving = lowest cost including those over the limit; null when it's the pick.
      - ties: cost, then DetourMin, then price, then id.
6. **UI:** trip mode in `BestPanel` ("Best on the way") at `?view=trip` (no coordinates in the URL; a cold link falls
   back to Best near you), its state in a scoped `TripState` service and in sessionStorage. `TripPushed` joins
   `CarsPushed` in the reload state. A trip is planned once, from where the user is; a Refresh action plans it again
   from the current location. Re-planning on its own while driving is for the later "follow me" feature.
   - The range chip ("Range 180 km · 08:10"), not shown for trips under 100 km. Tapping it edits the range; it's never
     asked again on its own.
   - Banners: "Stop before km 150", "No stop needed", "Low fuel: the closest station" (LowFuel), "No station you can
     reach on this route".
   - "Go via · Google Maps" with `waypoints` (check on Android that it works together with `dir_action=navigate`).
   - With 503 from `/trips/best`, or `ByRoad = false`, the "Going somewhere?" bar is hidden with a short message.
7. **Map:** `setRoute(path, halfWidthMeters)` / `clearRoute()` as GeoJSON layers inserted below the style's first symbol
   layer and re-added after `setStyle` (dark-mode switches drop custom layers), colours read from `--ess-accent`;
   `setDestination`, `setRangeEnd`. Trip stations come from the trip result and are merged into the markers like phase
   1's, since the viewport fetch stops below zoom 9; the "zoom in" message is suppressed in trip mode.
8. **Press and hold:** `contextmenu` (desktop right-click, Android long-press) plus a one-finger 550 ms touch timer for
   iOS, cancelled by movement over ~10 px, a second finger or a map move; ignored on markers; the next click swallowed so
   the hold doesn't dismiss the pane; `-webkit-touch-callout: none` on the map. `OnMapHeld(lat, lon)` to C#
   (`IMapListener.OnMapHeldAsync`). The dropped-pin card: Best on the way here, Save as Home, Work, or another place.
   Without Photon the label is "Dropped pin" and the distance. Keyboard path: "Use the map centre as destination".
9. **`SavedPlaces`** (Home, Work, others) and the Home/Work chips in the pane's "Going somewhere?" bar, so the commute is
   one tap from the pane. The phone chip also offers "To Work" once Work is saved.
10. **Changelog** `best-on-the-way.json`, `saved-places.json`, `road-distances.json`; **README**: API table, Deploy,
    Configuration (`Routing:BaseUrl`).

**Done when:** a trip from Montréal to Québec with 180 km of range says "stop before km 150" and picks a reachable
station within 10 min of detour (its KmAlong ≤ StopBeforeKm); a station on an autoroute service road shows a detour
above 0; a pin dropped in Ottawa or in a lake gets "No road near this point"; Best near you shows minutes; stopping the
routing container makes trips answer 503 and Best near you fall back to estimates, and the UI hides trip mode with a
message instead of breaking; a trip survives leaving for Google Maps and coming back.

## Phase 3: Address search (Photon)
**Ships:** "Going somewhere?" with autocomplete, addresses for dropped pins and saved places.

1. **Deploy:** `rtuszik/photon-docker:2.4.0` pinned by digest, `REGION=canada`, `UPDATE_STRATEGY=PARALLEL`,
   `UPDATE_INTERVAL=30d`, `JAVA_PARAMS=-Xmx2g`, `PUID`/`PGID` 1654, port 2322 not published, `mem_limit` about 4 GB,
   data on its own dataset (`/mnt/POOL/apps/essence/data/photon`, excluded from snapshots). The first start downloads
   4.6 GB and unpacks about 7.5 GB (about 20 GB during an update): either fill the folder once by hand before adding the
   service, or disable its healthcheck, or the autodeploy's 10-minute wait fails the rollout. Compose: profile `trip`.
2. **API:** `PhotonClient` (a plain 1.5 s `HttpClient.Timeout`, no retries: a stale keystroke isn't worth retrying;
   options `Geocoding:BaseUrl`, default `http://photon:2322/`), `GET /places/search?q&lat&lon&lang` (Québec `bbox`,
   location bias, results outside Québec dropped by `state`, 5 at most) and `GET /places/reverse?lat&lon`, under a
   separate rate-limit policy (`RateLimiting:Search:*`) so typing doesn't eat the shared 120 requests a minute. A
   Degraded health check.
3. **UI:** the search box (3 characters, 300 ms debounce, previous request cancelled, results cached by prefix), the
   "Set Work" sheet (suggestions, your places, pick on the map), reverse-geocoded labels for pins and places, "Addresses
   in Québec, from OpenStreetMap" attribution.
4. **Changelog** `address-search.json`; **README**: Deploy (dataset, first run), Configuration (`Geocoding:BaseUrl`,
   `RateLimiting:Search:*`).

**Done when:** typing "1000 de la gauch" suggests the Montréal address within about half a second; a search for an
address in Ontario returns nothing; with Photon stopped, search shows a message and pins still work.

## Phase 4 (optional): fuel level from the car (OBD2)
**Before starting:** the owner tests both cars with an OBDLink CX in a terminal app:
- Mazda3 (2018 GS): `ATZ`, `ATE0`, `ATH1`, `ATSP6`, `012F` → fuel % = A × 100 / 255.
- Corolla (2015 LE): `012F` is expected to fail; then `ATSH7C0`, `ATCRA7C8`, `2129` → litres = A / 2.

Then, in the web app only (Chrome on Android and desktop; the button is hidden where `navigator.bluetooth` is missing):
an `IFuelLevelSource` with a Web Bluetooth implementation (`essenceObd.js`) and its parsers in Essence.UI (tested in
Essence.UI.Tests). `requestDevice` first in the click path, every known service in `optionalServices` (FFF0, FFE0,
18F0, E7810A71-…), characteristics chosen by their properties, writes chunked to 20 bytes, the ELM init, then the car's
recipe (standard `012F`, or the Toyota request), the median of 5 reads. The profile gains the tank size and the recipe.
Range = litres ÷ consumption × 100, shown as "30 % · ≈ 212 km · from the car", and it replaces the typed range.
Changelog `fuel-from-the-car.json`.

## Cross-cutting
- **Rate limits:** best, trips and nearby share the per-IP budget (120 a minute); places get their own.
- **Accessibility:** real buttons, `aria-pressed` on toggles, the chip outside the live region, 44 px targets for the new
  controls (the toolbar keeps its 40–42 px buttons), a keyboard path for every map gesture.
- **French and English** for every string (the translations test checks the keys).
- **No schema change** in any phase: profiles and places live on the device, ranking runs on the snapshot.

## Out of scope for now
- **"Follow me"** (a later feature): re-planning while driving, prices that change during a long trip, and asking for the
  range again as it ages.
- Several stops on one trip: the driver plans again after the first stop.
- Accounts, or syncing cars and places between devices.
- The MAUI Android app and Android Auto.

Note: with the longest detour applied near you, the mockups' Costco 9 km away (about 30 minutes there and back, against
6 for the closest) is listed but no longer picked at the default 10 minutes; a car whose limit is raised still gets it.
