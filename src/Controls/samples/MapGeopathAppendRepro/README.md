# MapGeopathAppendRepro

This sample isolates the Android `MapElementHandler.MapGeopath` retained-options behavior for `Polyline`.

The repro models a common route-layer workflow:

1. Render a cached `Polyline` instance on a map.
2. Hide that route by removing it from `Map.MapElements`.
3. Keep the same route object alive and append more route points.
4. Show the route again by re-adding the same `Polyline` instance.

On Android, the `Polyline` keeps its `MapElementHandler` after removal. Mutating `Geopath` still updates the retained `PolylineOptions`, and `MapGeopath` appends the entire logical route into `PolylineOptions.Points` instead of replacing the existing points. Re-adding the same instance then creates a native Android polyline with the inflated point list.

With the default `2` initial points and `200` appended route updates:

- The fresh-instance control should create `202` logical and native points.
- The retained `polyline.Geopath.Add` path should inflate retained/native points to `20502`.
- The retained `polyline.Add` path should inflate retained/native points to `41002`, because that public API currently raises two handler updates per appended point.

The app reports these counts in the UI and writes autorun output to `autorun-results.txt`. Output includes:

- Elapsed time in milliseconds for initial render, off-map mutation, and re-add.
- Managed allocated MB, managed heap MB delta, and Android Java heap MB delta during the scenario.
- Unnecessary retained/native point entries created by the bug.
- Minimum unnecessary coordinate payload in MB, calculated as `extra point entries * 16 bytes` for latitude/longitude doubles. This is intentionally conservative and does not include Java object headers, JNI/global reference overhead, list capacity, renderer-side copies, or map SDK internals.

Heap deltas are useful context but can move downward if GC runs during a scenario. The stable impact signals are the elapsed milliseconds, managed allocated MB, unnecessary retained/native point entries, and minimum duplicated coordinate payload.

The default repro is intentionally higher than an average short route, because the impact is easier to understand at the scale where users start seeing slow redraws. The cost grows quadratically as a cached route is appended while its retained handler is alive. With `2` initial points and `500` appended points, the retained `Geopath.Add` path creates `251500` unnecessary retained+native point entries, or at least `3.84 MB` of duplicated coordinate payload before object overhead. In a local Pixel 9 Pro XL stress run, that `500`-append case crossed roughly `46k` outstanding GREFs and spent more than a minute in repeated full GC before the scenario could finish. With `1000` appended points, the retained `Geopath.Add` path would retain/re-add about `1,003,000` unnecessary point entries, or at least `15.30 MB` of duplicated coordinate payload before object overhead. The `Polyline.Add` path doubles the handler updates and would be about `30.64 MB` minimum duplicated coordinate payload.

On one Pixel 9 Pro XL autorun with the default `2 + 200` point scenario:

- Fresh control: `202` logical/native points, `70.2 ms`, `0.10 MB` managed allocated, `0` unnecessary point entries.
- Retained `Geopath.Add`: `202` logical points became `20502` native points, `886.6 ms`, `3.62 MB` managed allocated, `40600` unnecessary retained+native point entries, `0.62 MB` minimum duplicated coordinate payload.
- Retained `Polyline.Add`: `202` logical points became `41002` native points, `1714.1 ms`, `5.62 MB` managed allocated, `81600` unnecessary retained+native point entries, `1.25 MB` minimum duplicated coordinate payload.

The real-world issue is not that the default run is large; it is that ordinary route growth turns into repeated full-route duplication. More route updates or longer routes multiply the work sent back into Google Maps when the route is shown again.

## Build

```sh
dotnet build src/Controls/samples/MapGeopathAppendRepro/MapGeopathAppendRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY
dotnet build src/Controls/samples/MapGeopathAppendRepro/MapGeopathAppendRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

No Google Maps API key is committed. Pass it through `GoogleMapsApiKey`, which is forwarded to the Android manifest placeholder `MAPS_API_KEY`.

## Manual Android Run

Launch the app with a valid Android Maps API key.

1. Run `Run fresh control` and confirm native/output point counts match the logical route count.
2. Run `Run Geopath.Add repro` and confirm the retained/native point count is much larger than the logical route count.
3. Run `Run Polyline.Add repro` and confirm the inflated count is even larger.

## Android Autorun

```sh
dotnet run --project src/Controls/samples/MapGeopathAppendRepro/MapGeopathAppendRepro.csproj -f net10.0-android -p:GoogleMapsApiKey=YOUR_ANDROID_MAPS_KEY -p:MapGeopathAppendReproAutoRun=true
adb shell 'run-as com.microsoft.maui.mapgeopathappendrepro find . -name autorun-results.txt -print -exec cat {} \;'
```

The Android diagnostics use retained `PolylineOptions.Points.Count` and reflection over the current Android `MapHandler` native polyline list so the bad behavior is visible without visually inspecting map tiles.
