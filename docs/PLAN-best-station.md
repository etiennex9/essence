# Plan: Best near you and Best on the way

> **Status (2026-10-25):** designed with the owner through mockups and Q&A; every decision below was confirmed by the
> owner. Phases 1 to 3 are built (see "Phase 1 as built", "Phase 2 as built" and "Phase 3 as built" for where they
> differ from the steps); phase 4 isn't. Work in phase order: each phase ships on its own and is usable without the next.
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
  availability. The Costco test uses the brand slug: `Slugify` moved into Essence.Contracts (`Brands.Slugify`,
  `Brands.IsMembersOnly`) so the API and the UI share it. The snapshot sets each station's `MembersOnly` when it loads, so
  Essence.Ranking only takes a members-only flag and keeps no dependency.
- **Contracts** (positional `sealed record`s, shared with the UI; each needs a `[JsonSerializable]` in the UI's
  `EssenceJsonContext`). Money is `decimal`, each cost rounded to the cent (`MidpointRounding.AwayFromZero`), and Saves is
  the difference of the rounded costs, so the hero's figure matches the tiles. Sketch, to refine while building:
  - `BestNearby(RankedStation? Pick, long? ReferenceId, long? ClosestId, long? CheapestId, IReadOnlyList<RankedStation> Stations, double? RadiusKm, bool ByRoad)`
    (`RadiusKm` null: nothing priced within 25 km, `Stations` holds the closest priced station at any distance)
  - `RankedStation(Id, Name, Brand, Address, Latitude, Longitude, PriceCents, DistanceKm, DurationMin?, CostDollars, SavesDollars, MembersOnly, OverDetourLimit, SavingUncertain)`
    (the last two say why a station that saves isn't the pick)
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
   - `setEmphasis(pickId, closestId, listedIds, bestFlag, closestFlag)` toggling `is-best`, `is-closest`, `is-dimmed` (the
     markers not in `listedIds`) like `setSelected`, with the flags' text localized by C#, also
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

**Phase 1 as built** (where it differs from the steps above, or settles what they left open):
- The 2.0-factor safety rule recomputes both the station's and the closest's cost with roads twice the straight line.
- The response lists the pick, the closest, the cheapest, and the 4 lowest-cost stations that save among those that could
  be picked, then 4 among those that can't (past the longest detour, or uncertain by road): those mark the reason. The
  pane's "Also worth it" shows 3, pickable ones first. When the closest stays the pick although another saves, the hero
  says why (the longest detour, or distances being estimated) instead of "About the same".
- The pump opens the pane at once and then asks for the location (the pane shows "Finding your location…"), rather than
  opening it once located.
- `BestNearYou` asks as soon as the location is known, pane open or not, so the phone chip has a pick.
- My cars saves each change as it's made, so its button is Done rather than the mockup's Save. The car profile has no
  range fields yet: phase 2 adds the typed range and its time.
- The phone chip has shorter texts of its own (French didn't fit), with the distance beside "Best near you". The status
  chip's empty live region no longer takes room in `.status-slot`, so the chip sits right under the toolbar.
- The Google Maps link asks for driving directions (`travelmode=driving`); the hero's Go adds `dir_action=navigate`.
- A release reload that reopens Best near you keeps the view it had instead of fitting the map again.

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

**Phase 2 as built** (where it differs from the steps above, or settles what they left open):
- **Snapping without hints.** OSRM v26.10.0 returns every hint as zeros, and its `nearest` service ignores
  `snapping=any`, so hints can't carry the route's snapping into the tables. A call has one snapping for all its points,
  so the API first snaps the candidate stations alone with the profile's default (one table call, `OsrmClient.SnapAsync`:
  their own roads, autoroutes left out), drops those more than 300 m from their road (`Snapping.OnTheirRoads`), then asks
  for its tables with `snapping=any` on the points where the stations met their roads. The driver and a trip's ends snap
  to any road, as the route does, and the stations stay on theirs. Best near you makes three calls (snap, there, back), a
  trip four (route, snap, two tables). The 1 km check on the driver uses the table's source.
- **The service-road case**, against the real data: on the A-40, the Petro-Canada on the Dorval service road reads a
  0.1 km, 1.3 min detour (OSRM's own route through it: 0.3 km, 2 min), since the service road runs beside the autoroute
  between ramps; above 0, as asked, and inside the "on the way" thresholds. Montréal to Québec finds 13 stations on the
  way (1 before snapping was fixed).
- Best near you also falls back to estimates when the driver is more than 1 km from a road (the plan only said "OSRM
  down"), and an estimate forced by the server's failure is sent `no-store` without the import's version, or the same
  request would get a 304 and keep it after the server was back. By road it measures the 10 nearest and every station
  cheaper than the dearest of them (not than the nearest: the closest by road, the reference, may be another of the
  ten, across a river). `RankedStation.DurationMin` is the one-way time by road.
- `OsrmClient` and the response parsing live in Essence.Ranking (they need only `HttpClient`), so they're tested with a
  stub handler in Ranking.Tests; there's no API test project. The API registers it with the standard resilience handler
  (3 s per attempt, one retry after 200 ms, a circuit breaker that opens when half of at least 4 calls in 30 s failed,
  for 30 s) and a 1 s connect timeout. A stopped container's name takes about 4 s to fail to resolve on Docker Desktop, which no timeout shortens: the
  first two requests after it stops take 6–7 s, then the breaker answers in milliseconds. The health check is a `nearest`.
- Polly's own logs are off in the API (`"Polly": "None"`): it logged every failed attempt as an error with a stack trace.
  The `Essence.Api.Routing` category logs one line per failure, without the request path. nginx logs only critical
  errors under `/api/`: an upstream error names the request, query string included.
- **Contracts:** `TripPlan` adds `HalfWidthKm` (for the corridor on the map), `LongTrip` (the range counts) and
  `ReferenceOnTheWay` (else the reference is the smallest detour); `TripStation` has `DistanceKm`, start → station by
  road. 422s carry `point`: `from` or `to`. Points outside a Québec bounding box (44.9–62.7° N, 79.9–57° W) are refused
  before OSRM.
- **The range** counts only on trips of 100 km or more, in the API too (the UI always sends the car's range). Candidates
  past the range are cut at range + half-width, not range − spare + half-width, so the low-fuel case (the whole range)
  still has its stations, and the 5 first along the route are always kept. The bins are taken a round at a time (each
  bin's closest, then cheapest, second closest, second and third cheapest) until 100, then the cheapest of the rest.
  The biggest saving is also null when it saves under $0.25.
- **Not met as written:** a pin in downtown Ottawa by the river, or at Derby Line, Vermont, is under 1 km from a Québec
  road (Gatineau, Stanstead) and gets a trip there; south Ottawa, Edmundston, lakes and the Gulf get "No road near this
  point". A Québec boundary test (OSM's relation, simplified) would close it.
- **Durations** are OSRM's car profile as is: Montréal to Québec takes 3 h 26 min (about 2 h 40 by Google), since car.lua
  drives at 80 % of posted limits (`speed_reduction`). Detour minutes are consistent with it, but the longest detour is
  stricter than it reads. Left for the owner to decide (see the hand-over).
- **The image:** the healthcheck is `essence-routing health`, a curl of `nearest` (curl is installed for the build), not
  bash's `/dev/tcp`. The data also records the car profile's hash; serve warns (and still serves) when the image's
  differs. The refresh script builds with the routing image on the host when the app has no routing container (TrueNAS
  removes a stopped or failed app's containers) and leaves a stopped container stopped. Where `data` is a plain folder
  rather than a dataset, the routing dataset goes at `POOL/apps/essence/routing` (README). Measured here on the October 2026 extract: 1.16 GB download (22 min at Geofabrik's rate), 1.5 min of
  processing on 24 cores, a 6.6 GB RAM peak in osrm-extract, 909 MB of data, about 670 MB to serve it: `mem_limit: 2g`.
- **UI navigation:** `?view=trip` is pushed over Best near you (`TripPushed`); started from the bare map or another pane, it
  takes `ShowListAsync`'s place. The pane's Close goes back to Best near you and keeps the trip (Forward reopens it); the
  trip bar's ✕ ends it; the pump closes the whole pane (`history.go(-2)` when both entries were pushed). Started over
  details or My cars pushed over Best near you, the trip takes their entry and still counts as pushed. My cars over a
  trip keeps the trip on the map (`CarsOverTrip`, in the reload state too). A trip starts from a fresh location (the
  one known may be hours old), or from the map's centre when Best near you ranks from there.
- **The stored trip** includes its plan, so a restored tab shows it without asking the API again (planned once); it's
  planned again on load only when its plan doesn't answer the fuel and car (changed in another tab, or it had failed),
  once the map is up. After a 503, trips aren't offered for 2 minutes,
  nor while Best near you's answer is estimated.
- **The range chip** asks inline on long trips while the car has no range, edits it on a tap, and shows the time it was
  typed today, else the date. Typing it on the typical car sets the car up, as any change to it does.
- **"Going somewhere?"** is a box without search until phase 3, then Home and Work (dashed until saved: a tap says how)
  and the other places, all in one wrapping row; the hint says press and hold, or right-click with a mouse (by
  `pointer: fine`). Other places are forgotten from the trip's footer; at most 20 are kept.
- **The dropped pin's card** has no address until phase 3: "Dropped pin", the straight-line distance and the coordinates.
  Save names an other place inline; a pin saved before *Best on the way here* names the trip after the place.
- **Press and hold:** MapLibre fires `contextmenu` only after a `mousedown`, so Android's long press doesn't reach it; the
  550 ms touch timer covers every touch device, and right-clicks cover mice.
- **The map:** `setRoute(path, beyond, halfWidthMeters)`: the UI splits the path where the range runs out (`TripPath`).
  The layers go over every road and area, under the labels drawn after them: OpenFreeMap's dark style has labels before
  its roads, so "below the first symbol layer" hid the route there. The corridor fades out between zooms 10 and 13, or
  a short trip would tint the whole map. All the trip's stations are
  markers; those neither reachable nor saving at least $0.25 fade (the pick, the reference and the biggest saving never
  do), and the reference takes the closest's ring and the flag "No detour" or "Smallest detour".
- **Not checked:** Google Maps' `waypoints` with `dir_action=navigate` on Android (needs a phone).

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

**Phase 3 as built** (where it differs from the steps above, or settles what they left open):
- **A Québec-only index, built on the NAS** (the owner's choice once trimming turned out possible), instead of
  `rtuszik/photon-docker` with `REGION=canada`. That image only downloads whole countries: 7.5 GB of index (about 20 GB
  during an update) and a 4 GB memory limit. `src/Essence.Geocoding` is an image like routing's: Photon 1.3.0's jar
  (checksum-pinned) on `eclipse-temurin:21.0.12.1_1-jre-noble` (digest-pinned), runs as 1654, `serve` / `build` /
  `health`. `build` downloads Photon's JSONL dump of Canada from the mirror photon-docker uses (1.6 GB, MD5-checked),
  keeps the dump's header lines and the places whose address has `"state":"Québec"` or `"CA-QC"` (1.92 M of 10.2 M
  lines), imports them with `-languages fr,en` into `next/` and swaps it in, recording Photon's version (serve refuses an
  index from another) and the dump's date. Measured here: 8 min of import on 24 cores, a 3.2 GB RAM peak, 2.0 GB of
  index, healthy 9 s after a start, about 400 MB idle and 1.3 GB after 2,000 searches (median 22 ms, p95 52 ms at 16 at
  a time): `mem_limit: 2g`, a 1 GB heap. No `UPDATE_*`, `PUID`/`PGID` or `JAVA_PARAMS`: the image runs as 1654 and the
  monthly refresh is a cron job, as for routing. `deploy/truenas-routing-refresh.sh` became
  `deploy/truenas-data-refresh.sh <routing|geocoding> <folder>`, one cron job per service (geocoding an hour after
  routing). The first index is built by hand before the service is added, so the autodeploy's 10-minute wait isn't at
  risk; the healthcheck stays.
- **Names follow routing's:** the service, dataset and image are `geocoding` (`/mnt/POOL/apps/essence/data/geocoding`,
  `essence-geocoding`), and `Geocoding:BaseUrl` defaults to `http://geocoding:2322/`.
- **Photon's logs:** checked in Photon 1.3.0's source: Javalin's request logger is off and nothing logs a query
  (log4j: `de.komoot.photon` at INFO, the rest at WARN); there's no setting to turn down. `/api/places/` was already in
  nginx's no-log rule. The api logs one line per Photon failure under `Essence.Api.Geocoding`, without the request; a
  test captures every log line and checks that neither the typed text nor the coordinates appear.
- **The border, by Québec's boundary** rather than by reverse geocoding. With a Québec-only index the nearest place to a
  pin in Ottawa is in Gatineau; and even with Canada's, the index has no US places, so pins in Derby Line, Vermont came
  back as Stanstead. `QuebecArea.Contains` tests OpenStreetMap's boundary (relation 61549, simplified to 20 m south of
  49.5° N and 300 m north: 11,300 points, 56 KB, `src/Essence.Ranking/QuebecBoundary.txt`, made by
  `tools/quebec-boundary/make-boundary.py`; ODbL, credited in the file). `/trips/best` refuses a destination outside it
  with the same 422 as the other out-of-area cases (title "Outside Québec", `point: to`), with or without Photon:
  downtown Ottawa, Derby Line, Champlain NY, Edmundston and Labrador City are refused; Gatineau, Stanstead, Fermont and
  Blanc-Sablon aren't. The start is still checked against the box only (a driver just across the border gets the
  stations on the way in). `/places/reverse` answers the same 422 without asking Photon, and the pin's card says
  "Outside Québec" and offers neither a trip nor saving.
- **The endpoints:** `/places/search` asks Photon for 20 (5 are kept) with the Québec box, the bias, the layers worth
  driving to and `osm_tag` exclusions (waterways, natural features, land use, boundaries); results whose `state` isn't
  Québec are still dropped. Each place comes once: the same name and town (a long street's stretches), or the same name
  within 150 m (an address entered twice). A named place found only by its address (a business at "1000 de la
  Gauchetière") is put after the places whose own name matches. `PlaceSuggestion.Kind` is `address`, `street`, `town` or
  `named`; `Detail` is "Montréal, QC H3A 4W5" for an address. `/places/reverse` takes `lang` too, answers the nearest
  address within 100 m, else the nearest street or named place within 1 km (404 when none), and only Québec's places.
  Both take `lang` (`fr` by default, or `en`: Photon's `name:en`, "La Gauchetière Street West"). `RateLimiting:Search`
  is 60 a minute per IP, shared by search and reverse.
- **Tests:** `tests/Essence.Api.Tests` is new (WebApplicationFactory, no database): the endpoints' rules against a stub
  Photon, their own rate limit, and the logs. The trip's boundary check is covered by the boundary's tests and was checked
  against the running API.
- **The UI:** "Going somewhere?" is a button that opens `PlacesPanel` at `?view=places`, pushed over Best near you
  (`PlacesPushed` in the reload state), as a sheet opened at full height on phones so the box and the first suggestions
  stay above the keyboard. One panel serves the mockup's "Set Work" sheet too: its title follows what a place found is
  for (a trip, Home, Work, another place, which is named once found), and "Your places" has Set / Change / Cancel and
  Forget. The dashed Home and Work chips open it to set them (they used to show a hint). "Pick it on the map" closes the
  panes and the status chip says how. What a place found is for isn't kept across a reload (it reopens for a trip).
  `PlaceSearch` keeps answers per text, language and 10 km area (50 at most); while a longer text is on its way, the
  answers of the longest cached text it starts with show, cut to those it still matches, faded. Suggestions show the
  straight-line distance when the location is known, and the typed words in bold.
- **Addresses kept:** `SavedPlace.Address` and `TripDestination.Address` (short: "1000 Rue De La Gauchetière Ouest,
  Montréal"). A place found or a pin with an address names the trip ("To 1000 Rue…") and the map's destination; places
  saved before phase 3 get theirs when the panel shows them. Reverse lookups are kept for the session per point,
  failures aren't.
- **Not checked:** the keyboard over the sheet on a real phone (Android's keyboard overlays the page; the sheet opens
  tall for that), and the refresh script on TrueNAS. House numbers are OpenStreetMap's: some are missing (6200 Rue
  Saint-Hubert isn't there, 6235 is).

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
