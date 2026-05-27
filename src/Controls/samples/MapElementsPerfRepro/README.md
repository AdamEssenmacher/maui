# MapElementsPerfRepro

This repro targets [dotnet/maui#20502](https://github.com/dotnet/maui/issues/20502),
where adding many `Polyline` elements to `Map.MapElements` on Android can hang the app
and produce continuous GC pressure.

The repro is based on the public issue sample. The default issue scenario generates
`92` polylines with `500` locations each and adds each polyline to the map.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/MapElementsPerfRepro/MapElementsPerfRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY
dotnet build src/Controls/samples/MapElementsPerfRepro/MapElementsPerfRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Android manually:

```bash
dotnet run --project src/Controls/samples/MapElementsPerfRepro/MapElementsPerfRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY
```

The Android manifest uses `AndroidManifestPlaceholders` to insert the map key:

```xml
<meta-data android:name="com.google.android.geo.API_KEY" android:value="${MAPS_API_KEY}" />
```

No Google Maps API key is committed to the repo.

## What to Check

Run the scenarios in this order:

1. `Run small baseline`
   - Adds `8` polylines with `80` locations each.
   - This should complete and render map lines if Google Maps setup is correct.

2. `Run generation control`
   - Generates the configured polylines and locations but does not call `Map.MapElements.Add`.
   - This should complete quickly and isolates polyline generation cost from map insertion cost.

3. `Run issue repro`
   - Uses the configured values, defaulting to `92` polylines and `500` locations each.
   - On affected Android builds, this should severely slow or hang while adding polylines to `Map.MapElements`, during the post-render observation window, or through repeated GC pressure in `logcat`.

The app writes progress snapshots to `autorun-results.txt` in the app data directory.
Snapshots include elapsed time, generated/added counts, the last UI heartbeat, and the
largest observed UI heartbeat gap. On Android, read it with:

```bash
adb shell 'run-as com.microsoft.maui.mapelementsperfrepro find . -name autorun-results.txt -print -exec cat {} \;'
```

## Autorun

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/MapElementsPerfRepro/MapElementsPerfRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY -p:MapElementsPerfReproAutoRun=true
```

The watchdog runs on a background task. Each scenario keeps observing the UI after the
polyline add loop; the issue repro observes for the configured watchdog timeout, up to
30 seconds. If the UI thread stops heartbeating before the scenario completes, the
result file records `Status: Hung`, elapsed time, the last generated polyline, and the
last polyline successfully added to `Map.MapElements`.
