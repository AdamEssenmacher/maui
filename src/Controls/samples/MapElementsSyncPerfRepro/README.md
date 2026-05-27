# MapElementsSyncPerfRepro

This repro measures the real-world impact of repeated `Map.MapElements`
collection synchronization. It is meant to isolate the shared collection-sync
behavior from the Android-specific retained polyline builder problem in
dotnet/maui#20502.

By default the app uses `Circle` elements. Circles keep each map element cheap, so
the result is dominated by the cost of `Map.MapElements.Add` causing
`Handler.UpdateValue(nameof(IMap.Elements))`, `IMap.Elements` materializing a full
list, and platform handlers reconciling the full element set repeatedly.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/MapElementsSyncPerfRepro/MapElementsSyncPerfRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY
dotnet build src/Controls/samples/MapElementsSyncPerfRepro/MapElementsSyncPerfRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Android manually:

```bash
dotnet run --project src/Controls/samples/MapElementsSyncPerfRepro/MapElementsSyncPerfRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY
```

The Android manifest uses `AndroidManifestPlaceholders` to insert the map key:

```xml
<meta-data android:name="com.google.android.geo.API_KEY" android:value="${MAPS_API_KEY}" />
```

No Google Maps API key is committed to the repo.

## What to Check

Use `Run before/after suite` for the clearest signal. The suite writes an
`Impact summary` at the end of `autorun-results.txt` that compares the current
live-add behavior against a single-sync approximation.

The comparison is:

- **After approximation**: `Detached populate`, where the collection is populated
  before the map handler attaches. This approximates a batched or single-sync
  fix.
- **Before/current behavior**: `Live burst add` and `Live paced add`, where each
  `Map.MapElements.Add` happens after the map handler is live.

The summary reports:

- **Wall-clock update cost**: total scenario time minus the configured observation
  window and live-map settle delay. This captures the time a user waits for the
  visible map update to settle.
- **Effective throughput**: elements per second using wall-clock update cost, not
  just the managed add-loop time.
- **Managed add loop only**: included to show when managed code returns quickly
  while native queued sync work still blocks the UI afterward.
- **Max UI heartbeat gap**: the clearest UI-freeze metric.

Example Android result from a Pixel 9 Pro XL with `1000` `Circle` elements:

```text
Current live burst update cost: 12,190 ms
Single-sync approximation: 182 ms
Real-world improvement: 12,008 ms saved, 98.5% less waiting

Worst UI heartbeat gap: 20,020 ms -> 1,310 ms
Freeze improvement: 18,710 ms shorter, 93.5% less frozen time
```

This is the main signal the repro is designed to capture: the current
incremental live-add path can turn a simple map update into a multi-second wait,
while the same workload with one effective sync is under `200 ms` on the same
device.

You can also run the individual scenarios with the same element count and element
kind:

1. `Generation control`
   - Generates the configured elements into a plain managed list.
   - This captures allocation/generation cost without touching `Map.MapElements`.

2. `Detached populate`
   - Adds the generated elements to `Map.MapElements` before the map is attached
     to a handler.
   - This approximates one initial platform sync.

3. `Live burst add`
   - Attaches a map, waits for the live map handler, then adds every element in a
     tight UI-thread loop.
   - This measures repeated full-list sync work during incremental additions.

4. `Live paced add`
   - Attaches a map, waits for the live map handler, then yields after each add.
   - This approximates streaming updates and gives the platform handler a chance
     to process each collection change.

The result file includes elapsed time, generated/added counts, current
`Map.MapElements.Count`, managed memory before/after, UI heartbeat count, max UI
heartbeat gap, watchdog snapshots, and the final before/after impact summary.

On Android, read results with:

```bash
adb shell 'run-as com.microsoft.maui.mapelementssyncperfrepro find . -name autorun-results.txt -print -exec cat {} \;'
```

To jump straight to the final comparison:

```bash
adb shell 'run-as com.microsoft.maui.mapelementssyncperfrepro tail -n 80 files/autorun-results.txt'
```

## Autorun

Run all four scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/MapElementsSyncPerfRepro/MapElementsSyncPerfRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY -p:MapElementsSyncPerfReproAutoRun=true
```

If the UI thread stops heartbeating before a scenario completes, the background
watchdog writes `Status: Hung` with the last generated element, last added
element, elapsed time, and latest memory snapshot.
