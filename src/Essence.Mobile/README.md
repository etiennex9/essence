# Essence.Mobile

The Android app: a .NET MAUI Blazor Hybrid app that hosts `Essence.UI`, the same map and panels as the web app, in a
`BlazorWebView`. Android only (`net10.0-android`, package `io.github.etiennex9.essence`). Around the shared UI, it adds:

- the device's own location and storage (MAUI `Geolocation` and `Preferences`) in place of the browser's;
- three home-screen widgets: Best near where you are (or a place you choose), your favorite stations' prices, and Best on
  a route you drive often, which can follow where you are ([below](#widgets-that-follow-where-you-are));
- API calls through .NET's `HttpClient`, so no CORS entry is needed, each saying which build asks
  (`Essence-Android/<version name> (<version code>)`, see [API compatibility](../../README.md#api-compatibility));
- an update check against the GitHub releases of the repository that built it, shown in About.

## The server

The first launch asks for the server: a website's address, such as `<your domain>`, whose API the app finds under `api/`,
or at that address itself for an API on its own, as in development. A release build fills in the one it was built with,
if any ([ESSENCE_DEFAULT_SERVER](../../README.md#android-app-releases) secret), and accepts `https://` only. To change it later:
**About → Server → Change**. Favorites and the comparison are kept per server, since a station's id is its row in that
server's database; cars, places and the fuel are the driver's and follow along.

## Widgets that follow where you are

Best near you, and Best on the way from **Where I am**, rank from the device's latest location, kept by the app
(`LastLocation`). How up to date it is depends on what the user allows:

- **While using the app** (no new permission): the app's own fixes, and **↻** on the widget, which starts a short location
  foreground service (`WidgetLocationService`). Android lets a service started by a tap on a widget use the location while
  the app is closed. From Android 12 its notice rarely shows (Android holds one back for 10 s, and a fix takes less);
  before, it shows for the few seconds a fix takes.
- **Follow where I am** (About → *Widgets follow where you are* → Change, or a widget's settings → *Keep it up to date*):
  after saying what it's for, the app asks for *Allow all the time* on its own, as Android 11+ wants (it opens Android's
  settings). Then each 30-minute refresh takes a fix of its own (network, not GPS, within 5 s), and Android's passive
  location (fixes other apps already take) refreshes the widgets when the phone has moved 1 km or more, at most every
  5 minutes. *Turn off* forgets the choice; *Allow all the time* stays until changed in Android's settings, which the
  screen then offers.

The location goes only to the server, as the page's does (`/stations/best`, `/trips/best`). On the phone it's kept in a
preferences file of its own, `essence.location`, which Android's backups and transfers to a new phone leave out
(`Resources/xml/backup_rules.xml`, `data_extraction_rules.xml`). Once a fix is over 30 minutes old, the widget's footer
gives its time ("Location as of 6:10 a.m."), and says when it's approximate or the location is turned off. Without any
location permission, a fix kept from before still ranks; without one, the widget asks to allow it, or to turn the location
on. A trip from where you are that's
already at its destination says so, without asking the server.

On Samsung phones, One UI puts apps it thinks unused to sleep, which stops every background refresh: the Follow screen links
to its *Never sleeping apps*. A build for Google Play would leave all-the-time location out
(`-p:EssenceBackgroundLocation=false`): Google allows it for core features only. ↻ still locates.

To check it on a phone, give the shell a test location instead of moving:

```bash
adb shell appops set com.android.shell android:mock_location allow
```

```bash
adb shell cmd location providers add-test-provider network
```

```bash
adb shell cmd location providers set-test-provider-enabled network true
```

```bash
adb shell cmd location providers set-test-provider-location network --location 45.5017,-73.5673 --accuracy 20
```

Do the same for `gps` and `fused`, and `remove-test-provider` each one after. `adb shell pm grant|revoke
io.github.etiennex9.essence android.permission.ACCESS_BACKGROUND_LOCATION` switches *Allow all the time*, and `adb shell am
broadcast -a androidx.work.diagnostics.REQUEST_DIAGNOSTICS -p io.github.etiennex9.essence` logs WorkManager's jobs.

## Run it on a phone

Install the MAUI Android workload: Visual Studio's *.NET Multi-platform App UI development* workload, or

```bash
dotnet workload install maui-android
```

which then needs the Android SDK and a JDK 17 (Android Studio brings both; the build finds them through `ANDROID_HOME` and
`JAVA_HOME`, or `-p:AndroidSdkDirectory=` and `-p:JavaSdkDirectory=`). The workflow installs the SDK's parts the project
needs with `-t:InstallAndroidDependencies` ([android.yml](../../.github/workflows/android.yml)), which works locally too.

Run the back end in Docker ([Run it](../../README.md#run-it)), connect the phone with USB or wireless debugging (`adb devices`
lists it), forward the API's port to it, and build and start the app:

```bash
docker compose up -d postgres migrate ingest api
```

```bash
adb reverse tcp:8080 tcp:8080
```

```bash
dotnet build src/Essence.Mobile/Essence.Mobile.csproj -t:Run -f net10.0-android
```

A Debug build fills in `http://localhost:8080/`, which `adb reverse` takes to the API on your computer; run `adb reverse`
again after the phone reconnects. On the emulator, use `http://10.0.2.2:8080/` instead, the emulator's address for your
computer. Debug builds also take `http://`, which Android lets through to those addresses only.

Desktop Chrome inspects the app's page at `chrome://inspect` (Debug builds): console, DOM and network, as in the browser.
Work on the UI itself is faster in the web app, with hot reload (`dotnet watch --project src/Essence.Web`): it's the same
`Essence.UI`.

**Not in `Essence.slnx`.** Building it needs the MAUI Android workload, which the publish workflow's Linux test job and a
plain .NET SDK don't have, so `dotnet build` and `dotnet test` at the root leave it out. Build it by its path, as above.
Pull requests that touch it, `Essence.UI` or `Essence.Contracts` get an APK in the Android workflow's artifacts.

## A signed release, locally

The Android workflow makes the releases ([README → Versions → Android app releases](../../README.md#android-app-releases)).
To build one by hand, with the release's notes and the upload key:

```bash
dotnet run --project tools/Essence.Release -- notes src/Essence.Mobile/release
```

The build reads the keystore's password from an environment variable, which this asks for without keeping it in the
shell's history:

```bash
read -rsp "Keystore password: " ANDROID_KEYSTORE_PASSWORD && export ANDROID_KEYSTORE_PASSWORD
```

```bash
dotnet publish src/Essence.Mobile/Essence.Mobile.csproj -c Release -f net10.0-android \
  -p:ApplicationDisplayVersion=2026.11.07 -p:ApplicationVersion=<version code> \
  -p:EssenceDefaultServer=https://<your domain>/ -p:EssenceRepository=<owner>/<repository> \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=<path to essence-upload.jks> -p:AndroidSigningKeyAlias=essence \
  -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD -p:AndroidSigningKeyPass=env:ANDROID_KEYSTORE_PASSWORD
```

The APK and the App Bundle land in `src/Essence.Mobile/bin/Release/net10.0-android/publish/`, as `*-Signed.apk` and
`*-Signed.aab`.

| Property | |
|---|---|
| `ApplicationDisplayVersion` | The version name: the day `notes` printed, with dots. |
| `ApplicationVersion` | The version code: minutes since 2026-01-01 00:00 UTC (`$(( ($(date -u +%s) - $(date -u -d 2026-01-01T00:00:00Z +%s)) / 60 ))`). Android updates an app only to a higher one. |
| `EssenceDefaultServer` | Fills in the first launch's server. Empty by default; `http://localhost:8080/` in Debug. |
| `EssenceRepository` | `owner/name` on GitHub, whose latest release the update check reads. Empty: no check. |
| `EssenceUpdateCheck` | `false` turns the update check off; a version code of 1 never checks either. |
| `EssenceBackgroundLocation` | `false` leaves *Allow all the time* out of the manifest, and Follow where I am out of the app (a Google Play build). `true` by default. |
| `EssenceReleaseNotesDir` | The folder with `release.json` and `changelog-*.json` that About shows: `src/Essence.Mobile/release/` by default, ignored by git. Without them, About says *Development version*. |

The `notes` command takes its version from the first day tag containing HEAD, so run `git fetch --tags --force` first.
`adb install -r <the APK>` puts the build on a phone; over an installed release, Android accepts it only when it's signed
with the same key and its version code is higher.
