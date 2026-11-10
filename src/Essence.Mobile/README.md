# Essence.Mobile

The Android app: a .NET MAUI Blazor Hybrid app that hosts `Essence.UI`, the same map and panels as the web app, in a
`BlazorWebView`. Android only (`net10.0-android`, package `io.github.etiennex9.essence`). Around the shared UI, it adds:

- the device's own location and storage (MAUI `Geolocation` and `Preferences`) in place of the browser's;
- three home-screen widgets: Best near a place you choose, your favorite stations' prices, and Best on a route you drive
  often;
- API calls through .NET's `HttpClient`, so no CORS entry is needed, each saying which build asks
  (`Essence-Android/<version name> (<version code>)`, see [API compatibility](../../README.md#api-compatibility));
- an update check against the GitHub releases of the repository that built it, shown in About.

## The server

The first launch asks for the server: a website's address, such as `<your domain>`, whose API the app finds under `api/`,
or at that address itself for an API on its own, as in development. A release build fills in the one it was built with,
if any ([ESSENCE_DEFAULT_SERVER](../../README.md#android-app-releases) secret), and accepts `https://` only. To change it later:
**About → Server → Change**. Favorites and the comparison are kept per server, since a station's id is its row in that
server's database; cars, places and the fuel are the driver's and follow along.

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
| `EssenceReleaseNotesDir` | The folder with `release.json` and `changelog-*.json` that About shows: `src/Essence.Mobile/release/` by default, ignored by git. Without them, About says *Development version*. |

The `notes` command takes its version from the first day tag containing HEAD, so run `git fetch --tags --force` first.
`adb install -r <the APK>` puts the build on a phone; over an installed release, Android accepts it only when it's signed
with the same key and its version code is higher.
