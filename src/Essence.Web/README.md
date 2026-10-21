# Essence.Web

A single page: a fullscreen map that zooms to the device's location and shows nearby stations with the current price of the
selected fuel. Clicking a station opens its details: current prices and their recent history.

It's a standalone Blazor WebAssembly app. The page itself lives in the `Essence.UI` Razor class library, so a MAUI Blazor Hybrid
app can host the same components later (see [Android](#android) below). The browser calls `Essence.Api` directly.

## How it's built

| Piece | Where |
|---|---|
| Map | [MapLibre GL JS](https://maplibre.org/) 6.11.2 (BSD-3), vendored in `Essence.UI/wwwroot/lib/maplibre-gl/`. Tiles and styles from [OpenFreeMap](https://openfreemap.org/) (OpenStreetMap data, no API key). |
| Map ↔ C# | `Essence.UI/wwwroot/js/essenceMap.js` owns the map and the marker DOM; `Services/MapInterop.cs` drives it and receives moves, clicks and dismissals. |
| Page | `Pages/MapPage.razor`: fuel selector, locate button, status, data loading, `?station=` navigation. |
| Loading | `Services/StationViewport.cs`: on each move, `/stations/nearby` around the map center (radius to the corners, max 50 km and 100 stations; nothing below zoom 9), merged into a cache per fuel. |
| Markers | Callout pills (brand badge and price). Prices are tinted by tertile among the stations in view, the cheapest draws on top, and below zoom 12 markers shrink to brand-colored dots. Styles are in `wwwroot/css/essence.css`. |
| Details | `Components/StationDetails.razor` in `Pane.razor` (side panel from 768 px, bottom sheet below) and `PriceHistoryChart.razor` (stepped lines in plain SVG). |
| Favorites | The star in a station's details saves it on the device (`Services/FavoriteStations.cs`, through `IPreferenceStore`: `localStorage` here). The toolbar's star opens `Components/FavoritesPanel.razor`: the favorites, cheapest first for the selected fuel, each with a toggle to compare it, and *Compare all*. |
| Comparison | *Compare* in a station's details, or beside a favorite, adds it to the comparison (`Services/ComparedStations.cs`, also saved on the device, up to 8 stations: one per palette color, kept when others leave). The toolbar's chart button, with the count, opens `Components/ComparisonPanel.razor`: one price line per station for the selected fuel, and the stations cheapest first, each removable; *Clear the comparison* empties it. |
| History requests | Each listed or compared station costs one `/stations/{id}/prices` request (all fuels, so switching fuel is free), kept 2 minutes by `Services/HistoryCache.cs`. |
| Text | `Resources/Strings.resx` (English) and `Strings.fr.resx`. The language follows the browser. Fuel names stay as the Régie publishes them. |
| About | The button in the bottom-left corner opens `Components/AboutDialog.razor`, a native modal `<dialog>` (`wwwroot/js/essenceDialog.js`): the version and the changelog, a month at a time as it scrolls (`Services/ReleaseNotes.cs`). The files are under `release/`, written at build time ([Versions](../../README.md#versions)). |
| Updates | `Services/UpdateCheck.cs` reads `release/release.json` again, every 10 minutes and when the page is shown, and compares its commit with the one the tab started with. Another release shows `Components/UpdatePrompt.razor`, an offer to reload, while the page is in use; the page reloads by itself once it's been out of sight for a minute, or as it's brought back from that. `essenceMap.js` saves the view, the user's dot, the fuel and the history flags in `sessionStorage`, so the reload reopens as the page was. |

Opening a station adds `?station=<id>` to the URL, so Back closes the pane and the link can be shared; a link opened cold
centers the map on that station. The favorites add `?view=favorites` and the comparison `?view=compare`; a station opened
from them stacks on top, so Back returns to the list.

## Run it

With the back end running (`docker compose up -d postgres migrate ingest api` from the repository root):

```bash
dotnet watch --project src/Essence.Web
```

It opens on <http://localhost:5173>, which is the API's default `CORS_ORIGIN`. The API address comes from
`wwwroot/appsettings.json` (`Api:BaseUrl`, `http://localhost:8080/` by default).

Geolocation only works in a secure context: `https://` or `http://localhost`. To try it on an Android phone over USB, forward
both ports and open `http://localhost:5173` in the phone's Chrome:

```bash
adb reverse tcp:5173 tcp:5173
```

```bash
adb reverse tcp:8080 tcp:8080
```

When location isn't available or allowed, the map shows Montréal.

## Docker

`docker compose up --build` serves the published app with nginx on <http://localhost:8081> (`WEB_PORT`). nginx also forwards
`/api/` to the Compose service `api` (port 8080), adding `X-Forwarded-For`, `-Proto` and `-Prefix` headers, so the browser
reaches the app and the API at one origin. Behind a reverse proxy on a private network, it takes the client's address from
the proxy's `X-Forwarded-For`.

The image is the same for every environment: at container start, `nginx/40-essence-config.sh` writes `appsettings.json` from
these variables.

| Variable | Default | |
|---|---|---|
| `ESSENCE_API_BASE_URL` | `/api/` | The API as the **browser** reaches it: a path on this origin, or a URL whose origin is in the API's `Cors:AllowedOrigins`. |
| `ESSENCE_MAP_STYLE_URL` | OpenFreeMap `positron` | MapLibre style for the light theme. |
| `ESSENCE_MAP_DARK_STYLE_URL` | OpenFreeMap `dark` | MapLibre style when the device prefers dark. |

## Brand logos

Stations show a brand-colored monogram (E, PC, S…). To use a real logo, add `src/Essence.UI/wwwroot/brands/{slug}.svg` and
add the slug to `LogoSlugs` in `Services/BrandCatalog.cs` (listed so missing files are never requested). The slug is the
brand lowercased, without accents, with dashes: `Petro-Canada` → `petro-canada`, `Nutrinor Énergies` → `nutrinor-energies`.
Brand logos are trademarks; make sure you're allowed to use them. Colors for the biggest brands are in the same file.

## Android

.NET MAUI can't target the browser, so the shared part is `Essence.UI`. A MAUI Blazor Hybrid app (`src/Essence.Mobile`,
not created yet) would:

- host `Essence.UI`'s router in a `BlazorWebView`, with `essence.css` linked from its `wwwroot/index.html`;
- call `AddEssenceUI(...)` with a public API address (the Android emulator reaches the host as `10.0.2.2`);
- register its own `IDeviceLocation` (MAUI `Geolocation`) and `IPreferenceStore` (MAUI `Preferences`) before `AddEssenceUI`,
  because WebView geolocation needs extra permission plumbing on Android;
- handle the hardware Back button by navigating back in the WebView, which closes the details pane.

The API calls go through .NET's `HttpClient` there, not the browser, so no CORS entry is needed.
