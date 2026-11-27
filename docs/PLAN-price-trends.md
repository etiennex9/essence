# Price trends

The plan of the price trends (`/stations/trends`, Best near you's area card, the "Not up yet" and "Usually cheap"
tags, the Android alerts), with the owner's decisions and the figures the rules were set on. Settled: change a rule only
with new measurements.

> **Status (2026-11-27):** phases A and B are built (see "Phase A as built" and "Phase B as built"); C and D aren't.
> Work in phase order: each phase ships on its own. To resume: tell Claude *"Implement docs/PLAN-price-trends.md, phase C"*.

## Context
The owner wants to see price trends, not just "the lowest in the area in the last 24 h". Some of the ten ideas discussed
clash:
- "Savings vs the area median" would put a second saving figure next to Best's "Saves $X", which is counted against the
  closest station.
- Brand comparison and leader/follower add little, and leader/follower reads as accusing brands.
- Ideas 2–4 overlap.

Owner decisions (2026-11-24 to 26):
- **Build:**
  - (1) Show a jump in progress, with a green **"Not up yet"** tag on stations that usually follow the area's jumps.
  - (2+3+4, merged) One **area trends card**: lowest and median now, compared with the same time yesterday and last
    week; the 24 h low; a 24 h sparkline.
  - (5) The area's **pattern**, e.g. "usually go up around midnight; lowest in the evening". Cheapest weekdays are
    added once the data shows them clearly.
  - (7) A **"Usually cheap"** badge.
  - (9) **Android alerts**, last: a price threshold, plus a rise heads-up **only for rises that aren't the usual daily
    one**, with a user-set threshold.
- **Dropped:** brand comparison, leader/follower, savings vs the area median.
- **Area:** exactly Best near you's: `StationEndpoints.Eligible` then `SearchArea.Select` (10 km, or 25 km with fewer
  than 5 stations).
- **Placement:** the card goes right below Best's tiles, before "Also worth it". Nothing new goes above the tiles, so
  no tag or badge in the hero (phone sheet `min(60vh,600px)`).
- **Stale-price rule:** "Not up yet" is a scoped exception to the settled "no stale-price flag" rule. It shows only
  during a detected area-wide rise, only on stations that joined ≥ 60 % of the area's ≥ 8 past jumps, and stops 45 min
  after the followers stop rising (3 h at most).

What production showed: 45 days of history (2026-10-12 to 11-26), no gaps, 905 stations around 9 cities, Régulier,
each simulated every 5 minutes with the rules below. The local DB covers only about 44 % of that time.

| 10 km area | Area-wide rises | When | Rise |
|---|---|---|---|
| Montréal, Longueuil | 28–29 of the last 29 nights | 00:05 | +10–11 ¢ |
| Laval | 76 % of nights | 23:10–00:05 | +12 ¢ |
| Gatineau | about 1.2 a week, irregular | daytime, Mon/Tue/Thu | +7–15 ¢ |
| Saguenay | about 1 a week, irregular | late morning, Mon–Wed | +10–19 ¢ |
| Québec City, Sherbrooke, Trois-Rivières, Rimouski | 1 in 45 days | Oct 15, 19:05, province-wide | +10 ¢ |

- **About 70 % of a metro area raises.** A rise lasts 65–70 min (p90 160 min) under the rules below. Part of the
  remaining 30 % never follows, mostly Shell, Canadian Tire and Esso.
- **"Not up yet" works for followers.** Followers (≥ 60 % of ≥ 8 past jumps) still at the old price went up within
  3 h in **95–98 %** of cases (Montréal, Laval, Longueuil). For non-followers it was 52 %.
  - In Gatineau, followers built from only 5 jumps were right 10 % of the time, so the 8-jump minimum keeps the tag out
    there.
- **Time of day.**
  - The metro areas' mean price is lowest at 20–21 h, highest at 1–7 h, with a 9 ¢ spread. That holds on 86–98 % of
    days.
  - The area's cheapest price peaks around 8 h.
  - Other cities have a flat day (about 1 ¢ spread), so their pattern stays hidden.
- **A weekly hint.** Over 5–6 weeks, detrended:
  - Montréal is cheapest Thu–Fri (−1.8 / −2.6 ¢) and dearest Mon–Tue (+1.6 / +2.7 ¢);
  - Gatineau is cheapest Sat–Mon;
  - Saguenay is cheapest Sun–Mon.
  - It is shown only behind a strength test.
- **Comparisons.** The median vs yesterday moves ≥ 1 ¢ 35–80 % of the time; vs last week, almost always.
- **Short-lived prices.** 3.8 % of price rows last under 20 min, often flip-flops.
- **"Usually cheap" threshold.** Shares of time in the cheapest fifth:
  - ≥ 0.5 flags 8 % of Montréal and 5 % of Gatineau stations;
  - **≥ 0.4 flags 14 % and 12 %**, so the threshold is 0.4.
- **Two bugs the simulation found, both fixed below:**
  - a rise could end and come back when a late follower raised;
  - "unusual" duplicates followed from that.
- **No migration.** The API connects read-only (`essence_reader`), so everything is computed in memory in the API.
  Gap handling stays: it costs little, and the local DB needs it.

## Approach
- Work in a **new worktree off `main`** (`EnterWorktree`). This checkout keeps the uncommitted usage-counting work.
  - In the worktree, add a `.claude/launch.json` config pointing at the worktree's projects. The file is untracked and
    excluded.
  - Merge order: phase A and phase D touch files usage-counting also edits (`Program.cs`, the READMEs, Mobile
    services). Rebase on main after usage-counting merges.
- Four PRs, in order, each shipping on its own.
- Phase A commits `docs/PLAN-price-trends.md` with these decisions and constants, the way `docs/PLAN-best-station.md`
  does.
- **The contracts are settled in phase A.** After A merges they can only grow, with nullable trailing parameters
  documented "null from an older API". Draft B's view logic against them before A merges.

### Shared definitions (`src/Essence.Ranking`, pure; consts with doc comments citing the measurements)
- **Coverage**
  - Let g₀ < g₁ < … be the succeeded imports' `GeneratedAt`. A time t is covered if the latest gₖ ≤ t is at most
    10 min before t.
  - An import that follows an uncovered stretch is a *resume*. Its changes can lower a station's low, but never count
    as a rise.
- **Anchor A:** the latest import at or before `now` (injected `TimeProvider`, so `SimulatedTime` works). Nothing
  after A is read.
- **Late data:** late when `now − Snapshot.Version > DataVersion.ClockDependentFor`, the same rule as the 304. Late
  data means no jump.
- **Settled price**
  - A span counts if it is still open as of A (`To > A`) or lasted ≥ 20 min.
  - `priceAt(t)` is the latest settled span starting no later than **the latest import at or before t + 2.5 min**, and
    only when t is covered. That tolerance absorbs import jitter.
  - Unavailable means no price.
- **Lowest / median:** one value per station; the median is the lower middle value, so it is always a posted price.
- **Comparisons**
  - Yesterday and last week use the same local wall-clock time in `QuebecTime.ZoneFor(lat, lon)`, falling back to
    `A − days×24h` across DST.
  - Both medians use **only stations priced at both times**, and that count is returned.
  - A gap at either time gives null.
- **Sample grids:** anchored to local quarter-hours, not to A, so the cached results don't depend on when they were
  computed.
- **Constants**
  - Window: 56 d (6 weeks for the weekly test, 3 days on each side, and a week more so one bad day hides nothing). Reach:
    10 min. Settled: 20 min. Step: 15 min.
  - Jump:
    - lookback 4 h, in a fixed 7 h sweep window;
    - a rise is ≥ 5 ¢; a hold is ≤ 1 ¢;
    - needs ≥ 8 stations, with ≥ max(3, 25 %) of them risen;
    - its end is **chained**: `Until` starts at `Since + 45 min`, and each follower rise before the current `Until`
      moves it to that rise + 45 min, capped at `Since + 3 h`;
    - once passed, it is final, so a late follower can't bring the rise back.
  - Follower: joined ≥ 60 % of **≥ 8** past jumps within the pattern's 28 days.
  - Pattern:
    - 28 d, area ≥ 8 stations;
    - events use the jump rule, timed at the k-th rise;
    - a day counts only when it is fully covered from c − 3 h to c + 60 min;
    - needs ≥ 10 days, ≥ 60 % of them with an event, and ≥ 7 events in all;
    - lowest time comes from the **area's mean price**: shown when the spread is ≥ 3 ¢ and the hour is within ±2 h on a
      circular clock on ≥ 60 % of days;
    - it is stated as the day part holding the hours within 0.5 ¢ of the minimum (Montréal: 18–22 h, "in the
      evening").
  - Weekly:
    - from day means detrended by a centred 7-day average, over ≥ 6 weeks;
    - the 2 consecutive days with the lowest mean deviation are shown when it is ≤ −1.5 ¢ and negative in ≥ 75 % of
      weeks.
  - Cheap:
    - 30 d of 30-min samples;
    - in the cheapest fifth (mid-rank for ties) among ≥ 4 others;
    - shown at **≥ 40 %** of ≥ 336 counted samples.

### Phase A: trends engine and `GET /stations/trends` (API; PR labelled "web only")
- **Add in `src/Essence.Ranking`:**
  - `Coverage.cs`
  - `StationPrices.cs`
    - compact `PriceSpan` (24 bytes): tick fields plus an **`int` price in tenths**, −1 when unavailable;
    - row ids only for open spans;
    - an immutable copy-on-write `PriceWindow.With(...)`, idempotent by row id.
  - `Trends.cs` (now, yesterday, last week, 24 h low, `Last24h`)
    - The 24 h low's "until" is left out when its span was closed by a resume.
  - `Jumps.cs`
  - `PricePattern.cs`: the pattern, plus the set of stations that usually follow (`StationSet`, a sorted array).
- **Add in `src/Essence.Api`:**
  - `PriceTimeline.cs`, a singleton like `StationSnapshot`, keyed by `Snapshot.Version`:
    - first load: 56 d of imports, rows by `import_id` range in `valid_from` order, and the LATERAL state at the window's
      start (about 1 s, about 13 MB);
    - each new version loads only the new imports' rows and the closures of open rows;
    - trims once a day.
  - `TrendPatterns.cs`: the pattern and followers, the costly part (50–130 ms measured, 450 ms at most):
    - keyed by fuel + Costco + radius + the request point **snapped to a 1 km grid cell**, so panning the map doesn't
      create a new key per pixel;
    - computed once per cell per Québec day, one computation for the requests that ask at once, half the processors at
      most, and served stale while a background refresh runs;
    - a bounded LRU of 5,000 entries, about 1 KB each (5 MB at most).
  - `Endpoints/TrendEndpoints.cs`
  - Nothing here logs the request point.
- **Jumps:**
  - Sweep [A − 7 h, A]. Each station's low is its minimum within 4 h before its first rise of ≥ 5 ¢, and a resume never
    counts as a rise.
  - The jump starts (`Since`) at the k-th rise, so `Since` stays put. It ends at the chained `Until`.
  - Holdouts are followers that haven't risen, are within 1 ¢ of their low, and are at or below the midpoint of the
    medians before and after.
  - Stations touched by a resume are left out of both groups.
- **Contract** (additive, in `Contracts.cs`), as shipped:
  - `PriceTrends(Fuel, AsOf?, RadiusKm?, Stations, Now?, Yesterday?, LastWeek?, Low?, Last24h?, Jump?, Pattern?)`: `AsOf`
    and every figure are null before the first import.
  - `AreaPrices(At, LowestCents, MedianCents, Stations)`
  - `AreaChange(At, MedianCents, NowMedianCents, Stations)` for the two comparisons: the medians then and now of the
    stations priced both times, and how many.
  - `AreaLow(StationId, Name, Brand, PriceCents, From, To?, StillNow)`: `To` is null while it's still the price and when
    it ended during a gap.
  - `AreaSeries(From, StepMinutes, LowestCents[], MedianCents[])`
  - `PriceJump(Since, Until, Raised, Stations, RiseCents, BeforeCents, AfterCents, NotRaised: [NotRaisedStation(Id,
    PriceCents)], Usual)`
    - `Usual` is true when the pattern is strong and `Since` falls within ±90 min of `JumpsAround`. Phase D uses it.
  - `DailyPattern(JumpsAround?, LowestFromHour?, LowestToHour?, Days, CheapestDays?, Weeks)`
    - `LowestFromHour` / `LowestToHour` are the local hours of the low plateau, on a clock that goes round: 18 and 0
      for 6 p.m. to midnight.
    - `CheapestDays` is 2 **ISO day numbers, 1 Monday to 7 Sunday** (not .NET's `DayOfWeek`, where Sunday is 0), null
      until the weekly test passes.
- **Endpoint** `GET /stations/trends?lat&lon&fuel&costco=false&radiusKm=10`:
  - validation as `/best`; the `/stations` group with `RateLimiting.PolicyName`;
  - `.WithDataVersion(clockDependent: true)`; summary and description in the house style;
  - an empty area returns 200 with `Stations = 0`.
  - It is separate from `/best`: it doesn't depend on the car, makes no OSRM calls, alerts need it, and an older API's
    404 is a clean signal to hide the card.
- **Modify:**
  - `src/Essence.Api/Program.cs`
  - `src/Essence.Web/nginx/default.conf`: add `trends` to the no-log pattern.
  - An API test that reads that regex and asserts it matches every OpenAPI path taking lat/lon.
  - `README.md`: an API row and paragraph, the Last-Modified and no-log lists, and the `src/Essence.Ranking` and test
    rows.
- **Reuse:** `StationEndpoints.Eligible` / `ResolveFuelAsync`, `QueryErrors`, `SearchArea.Select`, `QuebecTime.ZoneFor`,
  `StationSnapshot.GetAsync`, `DataVersion.WithDataVersion` / `ClockDependentFor`, `Geo.DistanceKm`.

### Phase A as built
- **Successive jumps.** A test of the night with an evening batch showed that a batch of a quarter of the stations at
  21:00, over by 21:45, would have hidden the night's jump: `Jumps.Detect` finds the jumps one after the other in the
  sweep, each station counting in one only (its first rise), and gives the latest.
- **`AreaChange`** carries the median then and now of the paired stations, and **`AreaLow.StillNow`** says whether the
  lowest is still the price (its `To` is null both then and when it ended during a gap).
- **The window's start** is read with a LATERAL join in SQL: EF turned the same query in LINQ into a `ROW_NUMBER` over
  every older row.
- **Review fixes** (an adversarial review of phase A):
  - every rise of a station counts, not only its first: a station that went up in the evening, came back down and went
    up again at midnight is in the night's jump; and the jumps' bounds come from every rise, so an early riser cutting
    back can't bring an ended jump back;
  - a rise goes up from the price before (a cut that stays 5 ¢ above an old low isn't one);
  - a follower whose price hasn't changed in 2 days isn't listed as not up yet (`TrendRules.HeldChangedWithin`);
  - the window is 56 days, and a day's deviation is against the 7 days around it with one missing at most, so one bad
    day doesn't hide the days of the week;
  - the pattern cache computes a cell once for the requests that ask at once, at most half the processors at a time,
    and keys on the radius too;
  - the samples find the import they read once, not once per station, and nginx's no-log rule ignores case.
- **Measured locally** (232,000 prices, 7,254 station fuels): the first load takes 0.5 s, and a first request for a new
  cell (pattern computed) 50–130 ms. A 5-minute load test at about 18 requests/s at random points around 9 cities, all
  fuels: no error, p50 7 ms, p95 26 ms, p99 31 ms (436 ms at most, a first cell), and the API's memory flat at 188–198 MiB.

### Load and memory (phase A design rule)
- **Memory doesn't grow with traffic.**
  - One shared, immutable copy of the last 56 days: about 540k spans × 24 B ≈ **13 MB**, plus about 1 MB of index.
  - It grows only with the window (fixed) and how often prices change: 3× today's rate would be about 35 MB.
  - The API container has no memory limit and already keeps the station snapshot in memory the same way.
  - Requests only read the copy. Each new import swaps in a new copy, rebuilding only the ~35 stations that changed.
- **Work per request is bounded.**
  - The area lookup over the in-memory snapshot is the same scan `/best` already does.
  - The figures and the 24 h series take about 2 ms for 486 stations (the largest area, Montréal at 25 km); jump
    detection takes well under 1 ms.
  - The pattern comes from the cell cache; the first request for a cell in a day pays for it (50–130 ms).
  - At about 4 requests/s (≈ 1,000 people with Best open, one request per 5-min refresh), that is a few % of one core.
- **Shared work happens once.**
  - The first load takes about 1 s per API start, then a few small queries per import, behind a semaphore.
  - "Usually cheap" is computed once a day per fuel (about 0.5 s) in the background.
  - The database sees less load than if each request ran SQL. The 24 h state query alone took 17 ms and grew with the
    history.
- **Limits.**
  - The per-IP rate limit (120/min) still applies.
  - The client asks again only on a 1 km move, a new import, or after 10 min.
- **Logging.** The API logs the load time, span count and approximate size at Information on each full load (no
  coordinates), so a regression is visible.

### Phase B: area card and "Not up yet" (UI; Android release)
- **`src/Essence.UI/Services/AreaTrends.cs`** (scoped):
  - `BestPanel` attaches it when shown and detaches it when closed, so no requests go out while the panel is closed.
  - It fetches once on attach, then again only when:
    - the point moves more than 1 km;
    - fuel, Costco or `Best.DataAsOf` changes;
    - 10 min have passed.
  - It honours `RetryAfter`. A 404 sets `Unsupported` and hides the card. A failure keeps the last result.
  - `BestNearYou` is unchanged (its tests count requests).
- **`AreaTrendsView.cs`:** pure text and tag logic. The rule for "going up" vs "went up" is defined and tested.
- **`Components/AreaTrendsCard.razor` (+ scoped CSS, with the `.others-title` style copied in):**
  - a title, "Prices around here";
  - a jump line, when there is one;
  - a 4-figure grid: Lowest, Median, Since yesterday, Since last week. ▲ uses the warning colour, ▼ uses
    `--ess-good`, and null shows "—" (`Formatting.Price(null, culture)`);
  - a 300×36 sparkline via `ChartScale` and `HistorySeries.Area`. `Area` needs a label and colour, and empty series are
    dropped;
  - "Lowest in 24 h: … at …", named with `BrandCatalog.For(brand).Name ?? L["Independent"]`;
  - the pattern line, e.g. "Here, prices usually go up around midnight and are lowest in the evening", with ", and on
    Thursdays and Fridays" once the weekly test passes.
  - The card is hidden under 3 stations or when `RadiusKm` is null.
- **`BestPanel.razor` (+ CSS):**
  - Add `@inject AreaTrends`, and subscribe and unsubscribe to `Changed` in `Dispose`.
  - The card goes after the tiles block (lines 470–480), before `var others`.
  - A `Tag(station, arrival)` helper: the hours tag wins; otherwise "Not up yet" (`.tag.is-good`), and only while the
    station's current `PriceCents` is at or below its `NotRaised` price.
  - The tag goes on tiles and "Also worth it" rows only. `.tile .tag` gets `nowrap` and an ellipsis.
  - `_refreshAt = ArrivalView.Earliest(_refreshAt, jump.Until)`.
  - Add `@using Essence.Hours`.
- **Also:**
  - `EssenceApiClient.GetTrendsAsync` and `[JsonSerializable(typeof(PriceTrends))]`
  - `Formatting.Change`
  - `ServiceCollectionExtensions`
  - `--ess-good-bg` in `essence.css`
  - About 27 strings in `Strings.resx` / `Strings.fr.resx`, with typographic ’
  - `changelog/price-trends.json`
  - The README's UI-tests row

### Phase B as built
- **One rise line, no "went up":** the card and the tags say a rise is going on until its `Until`, then stop with no
  new answer (the pane renders again then); a rise that's over isn't told.
- **No sentence ends on a clock time in English** ("12:05 a.m.." would read the period twice): "Prices are going up
  since 12:05 a.m.: …", "Around 11:45 p.m., prices here usually go up.", "Lowest in 24 h: 139.9 at Esso (until
  12:10 a.m.)."
- **Shorter figure labels:** "Yesterday" / "Last week" ("Hier" / "7 jours"), with what they compare in their tooltip:
  "Since yesterday" was cut off in the desktop panel's 400 px.
- **The parts of the day** are 5: at night (0–5), in the morning (5–11), around noon (11–14), in the afternoon (14–17),
  in the evening (17–24), by the middle of the lowest hours (Montréal's 18–24 is "in the evening").
- **Checked in the preview** against the worktree's API at a simulated 00:20 (`Clock:Now`, `?now=`): the card under the
  tiles, the rise line, the figures, the 24-hour line, the low and the pattern; at 360×640 and 360×740, light and dark,
  the tiles' place unchanged, the tags on one line on all three tiles and in the rows (the tags forced by a test patch
  of the answer, since `/stations/best` reads today's prices while the trends read the simulated night), and the French
  labels measured to fit.

### Phase C: the "Usually cheap" badge (API + UI; Android release)
- **`src/Essence.Ranking/CheapStations.cs`:**
  - Each operating station is compared with its own 10 km area; members-only stations are left out of the comparison
    sets.
  - It uses the covered quarter-hour grid at 30-min steps, the mid-rank for the cheapest fifth, and an `int` price
    matrix.
  - It costs about 0.2–0.5 s of CPU per fuel per day.
- **`src/Essence.Api/UsuallyCheapCache.cs`:**
  - Inputs (`Snapshot`, `PriceWindow`) are read inside the request; only those immutable values go to the background
    computation, never a `DbContext`.
  - One task is shared by concurrent requests. It recomputes on a new Québec date or after 24 h, serving the stale
    result meanwhile.
- **Endpoint** `GET /stations/usually-cheap?fuel=` returns `UsuallyCheapStations(Fuel, ComputedAt, Days, Ids)` with
  `Cache-Control: public, max-age=3600`. It gets the summary and description; the README gets a row, a paragraph, and
  a note that this is the only answer without `Last-Modified`.
- **UI `Services/UsuallyCheap.cs`:** fetched once per fuel, again after 6 h; a 404 means no badges.
- **Badge (`.tag.is-good`):**
  - in StationDetails, on the selected fuel's price;
  - in `StationRow`, after the name;
  - in Best's "Also worth it" rows: the hours tag first, then "Not up yet", then "Usually cheap";
  - not in tiles and not in the hero;
  - the `.tag` / `.is-good` CSS is copied into StationRow's and StationDetails' scoped CSS.
- **Also:** strings and `changelog/usually-cheap.json`. The 0.4 threshold flags about 12–14 % of stations in production.
  Quiet, uniform towns may flag none, which is honest.

### Phase D: Android alerts (Mobile + UI; Android release)
- **Kinds** (at most 5 alerts), for Home, Work, a saved place, or where you last were:
  - **Price at or below X ¢/L**: through `/stations/best` with the active car, compared with `CheapestId`.
  - **Unusual rise**:
    - comes from `/stations/trends` when `Jump` is set, **`Usual` is false**, and the rise is at least the user's
      threshold (¢, default 5);
    - in production that's about once in 6 weeks in the Montréal metro, Québec City and the smaller cities (the Oct 15
      province-wide evening rise), and about once a week in Gatineau and Saguenay, mostly in the daytime;
    - the text names the cheapest holdout among Best's stations;
    - deduped per place: none again within 4 h;
    - hidden when the server's trends answer 404.
- **Shared, tested code** in `src/Essence.UI/App/Alerts/`:
  - `PriceAlerts.cs` (key `essence.alerts`), with an **`AlertsJsonContext`** source-generated context.
  - `AlertState.cs` (key `essence.alert-state`, added to `ServerScopedPreferences.ScopedKeys`).
  - `AlertChecks.cs`:
    - one request per place;
    - notifies when a price condition becomes true, at most once per 12 h, and re-arms above the threshold;
    - during quiet nights (22–07) it holds, then posts at 07:00 only if the condition still holds or the rise is still
      going;
    - skips a `LastLocation` older than 24 h.
  - Reuses `SavedPlaces`, `LastLocation`, `CarProfiles`, `BestQuery.For`, `StationHoursOverrides`, and `WidgetFeeds`'
    place resolution (make `StartOfAsync` and `Start` internal).
- **Android, `Platforms/Android/Alerts/`:**
  - `PriceAlertUpdates`:
    - JobScheduler ids 1_045_701/702, every 30 min, persisted, any network;
    - its lifecycle doesn't depend on the widgets;
    - cancelled when no alert is on or notifications are off;
    - scheduled from `App.xaml.cs` and from `IAppHost.AlertsChanged`.
  - `PriceAlertJob`:
    - a fixed Java name, a 30 s budget, `NotificationCompat`;
    - lock screen: `VisibilityPrivate`, with a public version;
    - the deep link is `essence://open/?station=ID`;
    - posted notifications are cancelled when the server changes.
  - Two channels, `POST_NOTIFICATIONS` in the manifest, strings in `values` and `values-fr`, and an icon.
  - Update the `RECEIVE_BOOT_COMPLETED` comment and the strings header comment.
- **`IAppHost`:** add `Notifications`, `RequestNotificationsAsync` and `AlertsChanged`, and list `Notifications` in the
  `Changed` doc.
- **`AlertsPanel.razor` (`?view=alerts`):**
  - added to `ListOpen` and the back stack;
  - **ignored without an `IAppHost`**, with a test;
  - explains, then asks, following `WidgetLocationActivity`.
- **About:** a "Price alerts · Change" line.
- **READMEs and changelog:**
  - Mobile README: a "Price alerts" section saying alerts are best effort (Doze, Samsung putting apps to sleep), that
    there is no server push, that the location notice may now show, and that "where you last were" stays fresh only
    with a placed follow-me widget.
  - Main README, Usage: add a sentence once usage-counting has merged.
  - `changelog/price-alerts.json`

## Verification
- **Tests** (xUnit v3, `FakeTimeProvider`, no database). `dotnet test` must pass each phase, including the
  `StringsTests` and `AndroidStringsTests` parity checks.
  - **Ranking:**
    - coverage and resumes;
    - spans (idempotent, flip-flops, trims, `int` prices);
    - trends: the even-count median, "yesterday" across the 2026-11-01 DST change, a gap giving null, paired medians,
      import jitter at sample times;
    - jumps on a Montréal-like night: holdouts are followers only; a non-follower, a station unchanged for 5 days, and a
      station with only 5–7 past jumps are not tagged; a two-stage evening rise doesn't move `Since`; a short gap
      mid-jump keeps the jump; a resume alone gives null; late data, more than 3 h, or 45 min without a follower rise
      gives null; a follower raising after the chained `Until` doesn't bring the rise back; fewer than 8 stations or
      +3 ¢ gives null;
    - the daily cycle: synthetic days with an evening batch give 00:00 and the evening low plateau; flat or random
      prices give null; a 5-station area gives null; the Magdalen Islands zone; `Usual` true and false;
    - the weekly test: 6 synthetic weeks cheaper on Thu–Fri give Thu–Fri; 5 weeks or a noisy sign give null;
    - cheap stations.
  - **API:** OpenAPI via `WebApplicationFactory` with a dead connection string, and the nginx no-log regex test.
  - **UI:**
    - `AreaTrends`: the panel closed sends no request; opening it with a result sends one; moving 300 m vs 2 km;
      `RetryAfter`; a 404;
    - `AreaTrendsView` texts in en-CA and fr-CA;
    - `HistorySeries.Area`;
    - the client URLs;
    - the alerts view ignored on the web;
    - `AlertChecksTests`, including `Usual` jumps never alerting.
- **Setup for live checks:**
  - Publish the compose postgres port with an override.
  - Stop the `api` container, which runs Production and ignores `Clock:Now`.
  - Run the worktree API as Development with `--urls http://localhost:8080`.
- **Phase A:** `curl /stations/trends`:
  - Montréal: about 162 stations, a median equal to the SQL lower median, a pattern of 00:00 and about 22–23 h;
  - Québec City: no pattern;
  - with `Clock__Now=2026-11-23T00:20:00-05:00`: `Since` about 00:05, followers among the holdouts, `Usual` true;
  - at 03:30: no jump;
  - a moment after a long gap: no false jump;
  - load time and memory;
  - **a load test:**
    - a script sends 20 requests/s for 5 min at random points around Québec's cities;
    - check p95 latency (target < 50 ms, ignoring first pattern computations) and API memory (`docker stats` /
      `dotnet-counters`), which must stay flat after warm-up;
  - nginx logs no trends line.
- **Phase B:** `preview_start` the worktree web app with `?now=…00:20…` on Montréal:
  - the card sits under the tiles, and followers' tiles show a green "Not up yet";
  - at **360×640 and 360×740**, in English and French, with the tag on all three tiles, the tiles stay fully visible in
    the sheet and the tag stays on one line;
  - dark mode;
  - at `?now=…03:30…` the tags are gone;
  - phone check in Firefox, per the sheet-height rule.
- **Against production**, after each phase's image is deployed:
  - `curl https://<your server>/api/stations/trends` for Montréal (pattern around midnight, lowest in the evening), Laval,
    Gatineau (no pattern) and Québec City;
  - compare with the figures above: they come from the same rules, simulated on the production history fetched through
    the public API at under 75 requests a minute;
- **Phase C:** about 12–14 % of metro stations flagged; spot-check flagged stations' 30-day charts; the badge appears
  in details, favorites, the comparison and Best's rows.
- **Phase D:** on the phone (`/phone`):
  - set an alert near Home above the current cheapest, then run
    `adb shell cmd jobscheduler run -f io.github.etiennex9.essence 1045701`: one notification, and tapping it opens the
    station;
  - running it again gives no duplicate;
  - it survives a reboot;
  - revoking `POST_NOTIFICATIONS` cancels the job and About says so;
  - a simulated unusual rise notifies and a usual one doesn't (through a local test server).

## Open points
- The weekly hint rests on 5–6 weeks. The strength test may keep it hidden for a few more weeks.
- "Not up yet" and the midnight pattern will show only in the Montréal metro. Elsewhere the card shows the figures,
  the sparkline and the 24 h low.
- The card's "Lowest" can differ from the Cheapest tile when that station is closed on arrival: trends ignore hours.
