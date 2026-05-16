# MapPoolLeakRepro

This repro targets the iOS/Mac Catalyst `MapHandler` path where disconnected `MauiMKMapView`
instances are returned to the static `MapPool` without clearing tracked `MapElement`s.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/MapPoolLeakRepro/MapPoolLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/MapPoolLeakRepro/MapPoolLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/MapPoolLeakRepro/MapPoolLeakRepro.csproj -f net10.0-maccatalyst
```

## What to Check

Use the default settings first:

- Pages/run: `30`
- MapElements/page: `40`
- Payload MB/page: `2`
- Dwell ms/page: `120`

Run these scenarios:

1. `Run control`
   - Pushes and pops the same real Shell map pages, but without `MapElement`s.
   - After full GC, alive `MAUI Map views` and `payload view models` should stay near zero.

2. `Run leaky MapElements`
   - Adds real `Circle` map elements to each map page.
   - The run first pushes the full stack, then unwinds it, so each map page has its own native map before anything is returned to `MapPool`.
   - On an unpatched build, alive `MAUI Map views`, `payload view models`, and `map elements` should grow with the page count after full GC.
   - On a patched build, these counts should return close to the control run.
   - `Payload retained by alive view models` is the clearest real-world impact number. With defaults, an unpatched build retains about `60 MB` of view-model payload, plus the retained MAUI map views/elements and native map state.

3. `Run mitigation`
   - Uses the same pages and elements, but clears `Map.MapElements` during page disappearance.
   - Counts should return close to the control run. This demonstrates that stale map elements retained by the pooled native map are the retention root.

The app forces full GC before measurements so retained weak references are meaningful. It also reports managed heap, GC heap, resident memory, and working-set deltas after collection.
