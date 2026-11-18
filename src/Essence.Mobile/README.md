# Essence.Mobile

The Android app: a .NET MAUI Blazor Hybrid app that hosts `Essence.UI`, the same map and panels as the web app, in a
`BlazorWebView`. Android only (`net10.0-android`, package `io.github.etiennex9.essence`). Around the shared UI, it adds:

- the device's own location and storage (MAUI `Geolocation` and `Preferences`) in place of the browser's;
- three home-screen widgets: Best near where you are (or a place you choose), your favorite stations' prices, and Best on
  a route you drive often, which can follow where you are ([below](#widgets-that-follow-where-you-are));
- API calls through .NET's `HttpClient`, so no CORS entry is needed, each saying which build asks
  (`Essence-Android/<version name> (<version code>)`, see [API compatibility](../../README.md#api-compatibility));
- updates from the GitHub releases of the repository that built it, which it downloads and installs itself
  ([below](#updates)).

## The server

The first launch asks for the server: a website's address, such as `<your domain>`, whose API the app finds under `api/`,
or at that address itself for an API on its own, as in development. A release build fills in the one it was built with,
if any ([ESSENCE_DEFAULT_SERVER](../../README.md#android-app-releases) secret), and accepts `https://` only. To change it later:
**About → Server → Change**. Favorites and the comparison are kept per server, since a station's id is its row in that
server's database; cars, places and the fuel are the driver's and follow along.

## How the widgets refresh

Every 30 minutes while one is placed, and right away when one is added or set up, on its **↻**, when the app leaves the
screen, after the server changes, and with Follow on, after a move ([below](#widgets-that-follow-where-you-are)). Each
refresh is one job for all the widgets, run once there's a network (`Platforms/Android/Widgets/WidgetUpdates.cs`). The
jobs are Android's JobScheduler's, not WorkManager's: WorkManager runs a job that's due in the app's process when that's
still there, and from Android 15 a process kept after leaving the screen has no network, so the widgets said they
couldn't be updated. To run the 30-minute refresh now:

```bash
adb shell cmd jobscheduler run -f io.github.etiennex9.essence 1045601
```

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
io.github.etiennex9.essence android.permission.ACCESS_BACKGROUND_LOCATION` switches *Allow all the time*.

## Corrected hours and OpenStreetMap

Hours the user corrects in a station's details are kept with the favorites, per server (`essence.hours`), so the widgets
go by them too and send them as `ignoreHours` ([README → Opening hours](../../README.md#opening-hours)). To send a
correction to OpenStreetMap as well, the user links their own account (About, or the hours editor) with the server's
OpenStreetMap application, which `/client-config` names ([README → Deploy → OpenStreetMap
edits](../../README.md#openstreetmap-edits)): register `essence://osm-auth` among its redirect URIs. The sign-in opens in
a Custom Tab (MAUI's `WebAuthenticator`, `Services/MauiOsm.cs`), and OpenStreetMap's answer comes back to
`essence://osm-auth`, which only `OsmAuthCallbackActivity` takes: the widgets' links (`essence://open/…`) go to
`MainActivity` by name, and `DeepLinks` never reads the sign-in's. The token is kept in MAUI's `SecureStorage` (Android's
keystore), not in the preferences the widgets read, and Android's backups and transfers leave its file out
(`io.github.etiennex9.essence.microsoft.maui.essentials.preferences.xml`): restored elsewhere it couldn't be decrypted,
and the user links again. The account isn't per server, but its token goes only to the API it was linked for: since any
server can be picked here, the app doesn't take a server's word for where OpenStreetMap is. It refuses one that names an
API other than its sign-in's own (the same host, or `www.` and `api.openstreetmap.org`) or an address that isn't https,
and a server naming another API than the account's asks for the account to be linked again.

To check it against OpenStreetMap's development server, point a local api at it (`OSM_CLIENT_ID`, `OSM_AUTH_URL` and
`OSM_API_URL` in `.env`) with an application registered there, run the app as below, and link from About.

## Updates

A release build looks for a newer build in its repository's latest GitHub release ([README → Android app
releases](../../README.md#android-app-releases)): when it starts, when it comes back, and from a background job, at most
every 12 hours (`Services/AppUpdateCheck.cs`). Once one is found, About says so, with a dot on its button, and the app
installs it itself (`Platforms/Android/Updates`):

- **In the background**, on Wi-Fi with the battery and storage not low: the job downloads the APK into the app's cache
  and checks it against the size and SHA-256 that GitHub lists for it. A minute after the app leaves the screen, or at
  the job's next run, it hands the APK to Android's `PackageInstaller`, which also checks that it's signed with the
  installed app's key. From Android 12, Android installs it without asking, once the user has allowed installing apps
  from Essence. Never while one of the app's screens shows, since installing closes the app. The job is Android's
  JobScheduler's, as the widgets' refreshes are ([above](#how-the-widgets-refresh)).
- **About → Install** downloads it on any network and installs it now. The first time, Android asks to allow installing
  apps from Essence (*Install unknown apps*); before Android 12, it asks each time. Allowing it restarts the app, while
  Android carries on with the install: the app finds that install among its sessions, and says *Installing* rather than
  offering Install again, which would install it twice. The app closes to update, and the user opens it again.

Google Play Protect may first ask to scan a build it hasn't seen (*App scan recommended*): the install waits for that
answer, and About says *Installing* meanwhile. Where it can't (a download or an install that failed, or an APK the
release doesn't list), About links to the release's page instead. A build that would have to ask in the background waits
for Install rather than trying again. Pull request builds don't check (version code 1). `-p:EssenceUpdateCheck=false`
turns it all off and leaves the two permissions, `REQUEST_INSTALL_PACKAGES` and `UPDATE_PACKAGES_WITHOUT_USER_ACTION`,
out of the manifest: a Google Play build would.

### Try the updates on a phone

Without making a release, build a debug app beside the installed release (its own package, so the release keeps its
data) at two version codes, with the APK holding its code (`EmbedAssembliesIntoApk`, which debug builds otherwise deploy
apart) and a feed of your own in place of GitHub's:

```bash
dotnet build src/Essence.Mobile/Essence.Mobile.csproj -f net10.0-android -p:ApplicationId=io.github.etiennex9.essence.debug "-p:ApplicationTitle=Essence (debug)" -p:IntermediateOutputPath=obj/side/ -p:OutputPath=bin/side/ -p:EmbedAssembliesIntoApk=true -p:ApplicationVersion=2 -p:ApplicationDisplayVersion=0.0.2 -p:EssenceRepository=<owner>/<name> -p:EssenceUpdateFeed=http://localhost:8099/latest.json
```

Copy `bin/side/io.github.etiennex9.essence.debug-Signed.apk` aside as `essence-2.apk`, build again with
`ApplicationVersion=3` and `0.0.3` into `essence-3.apk`, and serve that folder on port 8099 with a `latest.json` shaped
like GitHub's answer: `tag_name` (`android-0.0.3`), `html_url` (an `https://` page), and in `assets`, `name`
(`essence-3.apk`), `browser_download_url` (`http://localhost:8099/essence-3.apk`), `size` and `digest`
(`sha256:<hex>`). Then forward the port, install version 2 and open it:

```bash
adb reverse tcp:8099 tcp:8099
```

```bash
adb install -r essence-2.apk
```

About offers version 0.0.3: Install takes it from there. To see it install in the background, build a version 4,
point `latest.json` at it, and make the app forget when it last checked: stop it, since it keeps its preferences in memory,
then edit them (debug builds let `run-as` at their files):

```bash
adb shell am force-stop io.github.etiennex9.essence.debug
```

```bash
adb shell run-as io.github.etiennex9.essence.debug sed -i /essence.app-update-checked/d shared_prefs/io.github.etiennex9.essence.debug_preferences.xml
```

Open the app, which finds it, and leave it: a minute later, on Wi-Fi, it updates without a word.
`adb logcat -s Essence.Update` shows why one didn't install. Rebuild each version rather than reusing a version code: the
app keeps what it last found, and an APK that isn't the one it found doesn't check out. Afterwards, `adb install -r -d`
puts a build of version code 1 back (a debug build may go down a version), so that the usual builds install over it.

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
| `ApplicationDisplayVersion` | The version name: the day `notes` printed, with dots, and from that day's second release on, its number: `2026.11.07`, `2026.11.07.2`… About shows it when the version code is above 1. |
| `ApplicationVersion` | The version code: minutes since 2026-01-01 00:00 UTC (`$(( ($(date -u +%s) - $(date -u -d 2026-01-01T00:00:00Z +%s)) / 60 ))`). Android updates an app only to a higher one. |
| `EssenceDefaultServer` | Fills in the first launch's server. Empty by default; `http://localhost:8080/` in Debug. |
| `EssenceRepository` | `owner/name` on GitHub, whose latest release the update check reads. Empty: no check. |
| `EssenceUpdateCheck` | `false` turns the [updates](#updates) off and leaves the permissions to install out of the manifest; a version code of 1 never checks either. |
| `EssenceUpdateFeed` | Read instead of GitHub's latest release, to try the updates on a phone ([below](#try-the-updates-on-a-phone)). Empty by default. |
| `EssenceBackgroundLocation` | `false` leaves *Allow all the time* out of the manifest, and Follow where I am out of the app (a Google Play build). `true` by default. |
| `EssenceReleaseNotesDir` | The folder with `release.json` and `changelog-*.json` that About shows: `src/Essence.Mobile/release/` by default, ignored by git. Without them, About says *Development version*; with them and a version code of 1, it shows their day as the version. |

The `notes` command takes its version from the first day tag containing HEAD, so run `git fetch --tags --force` first.
`adb install -r <the APK>` puts the build on a phone; over an installed release, Android accepts it only when it's signed
with the same key and its version code is higher.
