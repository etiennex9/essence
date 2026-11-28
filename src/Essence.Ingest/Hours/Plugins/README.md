# Hours plugins

A plugin is an hours source written as a spec: a JSON file with a name, a label, a URL and, only when needed, a few
hints. One generic engine (`HoursPluginSource`) reads every plugin's answer. It runs on the same schedule as the
sources written in code, once a week at a random time in the night, with the same retries, cache, re-match, gate and
alerts (README → Opening hours).

- **Shipped specs** live here (`*.json`), embedded in the ingest's image and checked by the tests
  (`HoursPluginSpecTests`, `HoursPluginSourceTests`).
- **The server's specs** live in `Hours:PluginsPath` (`/data/hours-plugins` by default, on the ingest's data volume).
  They're read on each start, so they need no new image. A spec named like a shipped one replaces it. A spec must be
  UTF-8, and each field of the kind it's documented as (`"matchRadiusM": "300"` refuses it, it isn't taken as the
  default). A spec that can't be read is logged ("Hours plugin skipped: …") and not run. While any is there, or the
  folder can't be listed, no source's hours are removed on that start: whose spec it was can't be told (it may be one
  renamed, or a typo), so a broken spec never deletes a source's hours. One that replaced a shipped spec falls back to
  it. A broken spec never stops the ingest.
- **A spec whose URL, `params`, `where`, `format`, `province` or `fields` changed** no longer reads its last download,
  which the old spec made: the plugin runs as the ingest starts, rather than at its turn up to a week away. The spec
  each good run was made under is kept (`hours_sources.good_spec`), so the first run under another is gated as a new
  source's, whether the old download is still on disk or not: a spec narrowed on purpose (a filter, another province)
  isn't rejected for matching fewer stations; one that gives no records at all still is (turn the plugin off instead).
  A new version of the engine (`HoursPluginSource.Engine`) runs every plugin at once too, but gated as always. A change
  of its label, brand, link or `hours.unknown` applies to the download kept, at the next re-match.
- **To turn a plugin off**, set `"enabled": false` in its spec, or set `Hours__Plugins__<name>__Enabled=false`. Its rows
  are removed on the next start, as for any source turned off.

## Before adding a source

- **Never work around bot protection** (Cloudflare, Akamai, a captcha). Petro-Canada, Esso and Canadian Tire are behind
  it.
- **Read the site's robots.txt and terms** for the URL you'll ask. Put what you found in the spec's comment.
- **Ask for as little as possible.** One request a week is the norm. Use `params` only for a locator that answers by
  point or box.

## Try a spec

```
dotnet run --project src/Essence.Ingest -- --try-hours-plugin path/to/spec.json
docker compose exec ingest dotnet Essence.Ingest.dll --try-hours-plugin crevier
```

It asks the URL once, as a run would, and prints:

- where it found each field;
- how many records have hours, and the first ones with their rules;
- the warnings.

It writes nothing and needs no database.

## The spec

```jsonc
{
  // Comments and trailing commas are fine.
  "name": "crevier",               // required: a-z, 0-9 and '-', 2 to 31 characters; stored as the source's name
  "label": "Crevier",              // required: how the app names the source ("Hours from Crevier")
  "kind": "brand",                 // "brand" (trusted, default) or "open-data" (only shown)
  "rank": 50,                      // lower first; default 50 for a brand, 80 for open data
  "brand": "Crevier",              // the brand of a record that gives none
  "link": "https://…",             // where a person checks the hours, for a record without a page of its own
  "url": "https://…",              // required; may hold {placeholders} filled by "params"
  "params": [{ "lat": 45.5, "lon": -73.57 }],  // one request each, the records put together, each id once
  "where": { "hasGasDepartment": true },       // keep only the records whose field is this value (or one of a list)
  "format": "auto",                // auto | json | csv | html
  "province": "QC",                // keep the records in this province, or that don't say; null for all
  "matchRadiusM": 150,             // how far from a station of its brand a record may be (10 to 2000)
  "otherBrandReachM": null,        // open data: how far a record of another brand may be (see HoursMatcher)
  "enabled": true,
  "fields": { "id": "harnoisID" }, // hints, only where the auto-detection picks wrong (below)
  "hours": { "unknown": ["00:00-12:00"] }      // ranges the source gives when it doesn't say: the record is left out
}
```

The ranks of the sources in code are Costco 10, `curated` 20, Ultramar 30, Shell 31, Couche-Tard 40 and OpenStreetMap 60
(61 when not confirmed). A plugin may later take over one of those sources' names and ranks, once its class is gone. It
then keeps that source's rows, history and alerts.

## What's found on its own

| Step | How |
|---|---|
| The answer | JSON as it is. CSV becomes rows keyed by their header; the delimiter (`,`, `;` or a tab) and the encoding (UTF-8, else Windows-1252; a spec itself must be UTF-8) are detected. HTML becomes the page's `application/ld+json` and `application/json` blocks, each `@graph` spread; a block that isn't JSON is skipped. The format comes from the first character. JSON with a key twice in an object isn't read: in a JSON answer, it fails the run. |
| The records | The list of objects where most items have a point. This can be an object of objects keyed by id (`{"stores": {"1952": {…}}}`), nested anywhere. An answer that says more pages follow (`hasNextPage`, `hasMore`), or whose `success` or `ok` doesn't say yes (`false`, `"false"`, `0`, `null`), fails the run. |
| Each field | Found once per answer, from its name in French or English **and** its values across the records:<br>• **id**: unique to each record; else the key of a keyed object; else the point.<br>• **point**: numbers in range, by name, or a GeoJSON `coordinates`. Swapped latitude and longitude are caught.<br>• **postcode**: values shaped `A1A 1A1`, else one found in the address.<br>• **province** and **country**: values that are ones. Without a province, it comes from the postcode.<br>• **address**: most values start with a house number.<br>• **brand**, **link** (http values) and **status**: a field named `status` or `…Status` with some words it knows (active, open, ouvert, temporary closure, fermé temporairement, permanently closed…). A record whose status is a word it doesn't know, or empty, is left out: it can't be told open. |
| The fuel's part | An item of a list whose type is the forecourt's, the fuel's or the gas bar's (`services[serviceType=Forecourt]`). When it gives hours, its hours and its status come first. A place closed for now whose forecourt says open is left out, since which is right can't be told. |
| The hours | A field whose name is only words of hours and of the place: `hours`, `openingHours`, `horaire`, `heuresDOuverture`, `schedule`, `periods`, `siteRegularHours`, `gasStationHours`, `openingHoursFuel`, `location.hours`. Names with fuel, gas or forecourt come first, and the first of those that gives hours is read. Other fields that give hours must agree, as schema.org's `openingHours` and `openingHoursSpecification` do: two that differ, or one that can't be read, leave the record out. A flag for 24 hours (`twentyFourSeven`, `alwaysOpen`, `open24h`…), or a service or tag that is exactly "24h", "24hrs", "24 heures", "Ouvert 24h" or "24/7", means 24/7. |
| Not the station's | A field named for anything else, or inside one, is never taken for the hours, the 24-hour flag or the status: `timHortonsHours`, `epicerieHours`, `carWash.open24h` may be another department's. A brand whose station's hours are named so needs a `hours` hint. Tags are the station's only in a list named for them (`services`, `amenities`, `features`…), by keys that say nothing of whose they are (`{"1": "24hrs"}`, Harnois'), or in items whose type, if any, is the station's or its fuel's: `{"epicerie": "24h"}`, `{"atm": "24/7"}` and `{"type": "Tim Hortons", "name": "24h"}` aren't. |
| Special hours | schema.org's `specialOpeningHoursSpecification`, and fields of special, exceptional, temporary or seasonal hours: each entry must be dated (`validFrom`, `date`…), being a holiday's or a season's; one that isn't leaves the record out, since it may be the week's. Holidays' own fields are left to the holidays' caution. |
| Country | Every field named for the country must say Canada (Ultramar's `country` and `siteCountry`): "US", "FR" or a word it doesn't know leave the record out. |

The hours are read in any of these shapes:

- an opening_hours rule (`Mo-Fr 06:00-22:00`), or schema.org's `openingHours` list;
- days with open and close times (`dayOfWeek`/`openTime`/`closeTime`, schema.org's `openingHoursSpecification`, whose
  entries for a season are skipped);
- an object keyed by day (`{"monday": {"open": …}}`, `{"Lun-Ven": "6h-22h"}`);
- lines of days and times (`"Mon-Fri."`, `"6:00am - 9:30pm"`);
- Google's `periods`;
- **hours as people write them**, in French or English, for example "Lun-Ven : 6 h à 22 h; Sam. et dim. 7h-21h", "Du
  lundi au vendredi de 6h00 à 22h00", "Mon-Fri 6am-10pm", "Tous les jours 6h-23h", "Ouvert 24 h/24, 7j/7",
  "Fin de semaine : 8 h à minuit". A line of holidays alone is skipped; the days named with them ("Dimanche et jours
  fériés : 8 h à 20 h") get its hours.

A day is closed only when it says so: "Fermé", `closed: true`, `isOpen: false`, or a day left out of Google's `periods`.

**Nothing is guessed.** Each of these refuses the hours, and the record is left out with a warning:

- a word it doesn't know ("sauf", "été", a note), or a field next to a day's times that isn't one ("carWash");
- a day left out, null, or given twice: on two lines, by two entries that don't name the same days ("Lun-Dim" then
  "Dimanche: fermé"), as open and as closed (`["6h-22h", "Fermé"]`), beside a list of hours (`{"periods": […],
  "sunday": "Fermé"}`, `"sundayClosed": true`, `"closedOn": "Sunday"`), as a field of another day's hours
  (`"sunday": "closed"`), or by an entry inside them that names other days or a day it can't read (`{"day": 0}`,
  "Sundays only"). Several entries for the same days are split shifts;
- open and close times that aren't text it reads (`""`, `600`, `{"hour": 6}`), or times on the 12-hour clock (am, pm,
  "noon", "midnight") anywhere in the hours with others, in digits from 1 to 12, that could be either ("1:00 - 6:00 pm",
  "noon - 6:00", "6am-10pm, 1:00-6:00"), Google's periods included. French hours ("6 h", "18h30", "minuit") are the
  24-hour clock's;
- in a day's entry, a flag that isn't true or false (`"closed": "true"`, `"isOpen": [false]`), any other flag it
  doesn't know (`dayOff`, `available`, `active`, `enabled`, `isHoliday`), a status that doesn't say open (`"status":
  "closed"`, `"état": "fermé"`, `{"code": "closed"}`), a text that says closed or open in a field it doesn't know
  (`"note": "Fermé"`), or flags that say closed and open (`closed: false` with `isOpen: false`). A period is only its
  open and close, each a day and a time. Flags of the moment (`isOpenNow`, `isToday`) are let be;
- ranges that overlap, a range from `hours.unknown`, a week closed every day, or schema.org's "00:00" to "00:00"
  (closed there, all day elsewhere).

The station's other sources serve instead. A record without a point is always left out: without one, it would be a
rule for every station of its brand.

## Hints

Each hint is a path in a record:

- `a.b`: a field inside another.
- `a[]` or `a.*`: each item of a list, or each value of an object.
- `a[key=value]`: only the items whose `key` is `value`, in any case.
- `a[0]`: the first item.
- `a | b`: the first path that gives anything.

The fields that take a hint are `records` (in the answer, not in a record), `id`, `latitude`, `longitude`, `address`,
`postcode`, `province`, `country`, `brand`, `link`, `status`, `hours`, `allDay`, `allDayTags` and `fuel`, each once
(`lat` and `latitude` both refuse the spec). A `hours` hint
replaces the hours found, and the fuel's part is then only used when `fuel` is given too. The mapping used is printed by
the try command and kept with the download in `/data/hours/<name>/`.
