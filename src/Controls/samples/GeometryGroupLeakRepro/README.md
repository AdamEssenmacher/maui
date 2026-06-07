# GeometryGroupLeakRepro

This repro targets the shared `GeometryGroup.Children` path where `GeometryGroup` subscribes to every child geometry's `PropertyChanged` event. `GeometryCollection` is an `ObservableCollection<Geometry>`, so `Children.Clear()` raises a reset notification without `OldItems`. `GeometryGroup.OnChildrenCollectionChanged` only unsubscribes `OldItems`, leaving removed child geometries subscribed.

When those removed child geometries are app-level shared vector fragments, the retention path is:

    shared Geometry fragment
      -> PropertyChanged delegate
      -> page-local GeometryGroup
      -> PropertyChanged delegate from Path.Data
      -> Path
      -> dashboard page and BindingContext payload

The sample uses realistic dashboard pages with shared vector fragments, 24 card icons per page, and cached case-file payloads to show the practical impact.

## Run

Build from the repo root:

    dotnet build Microsoft.Maui.BuildTasks.slnf
    dotnet build src/Controls/samples/GeometryGroupLeakRepro/GeometryGroupLeakRepro.csproj -f net10.0-android
    dotnet build src/Controls/samples/GeometryGroupLeakRepro/GeometryGroupLeakRepro.csproj -f net10.0-maccatalyst
    dotnet build src/Controls/samples/GeometryGroupLeakRepro/GeometryGroupLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64

Run Mac Catalyst locally:

    dotnet run --project src/Controls/samples/GeometryGroupLeakRepro/GeometryGroupLeakRepro.csproj -f net10.0-maccatalyst

Run the three scenarios automatically on Mac Catalyst and write a result file:

    GEOMETRY_GROUP_LEAK_REPRO_AUTORUN=1 \
    GEOMETRY_GROUP_LEAK_REPRO_RESULTS=/private/tmp/geometrygroupleakrepro-results.txt \
    dotnet run --project src/Controls/samples/GeometryGroupLeakRepro/GeometryGroupLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/geometrygroupleakrepro-results.txt

Run the three scenarios automatically on Android:

    dotnet run --project src/Controls/samples/GeometryGroupLeakRepro/GeometryGroupLeakRepro.csproj -f net10.0-android -p:GeometryGroupLeakReproAutoRun=true
    adb shell run-as com.microsoft.maui.geometrygroupleakrepro find . -name autorun-results.txt -print -exec cat {} \;

## What to Check

Use the default settings first:

  * Pages/run: `50`
  * Payload MB/page: `4`
  * Cards/page: `24`
  * Shared fragments/card: `6`
  * Dwell ms/page: `50`

Run these scenarios:

  1. `Run control`

     * Builds the same dashboard UI and still calls `GeometryGroup.Children.Clear()`.
     * All transient geometries are page-local, so the stale event cycle has no long-lived root.
     * After full GC, alive pages, payloads, paths, and geometry groups should stay near zero.

  2. `Run shared fragments`

     * Seeds every page-local `GeometryGroup` with shared app-level vector fragments, calls `Children.Clear()`, and then rebuilds visible page-local geometry.
     * On an unpatched build, the shared fragments retain the cleared groups, their `Path.Data` subscribers, the dashboard pages, and the cached payload view models.
     * With defaults, an unpatched build should retain about `200 MB` of view-model payload, plus retained pages, layouts, paths, geometry groups, and native handlers.

  3. `Run mitigation`

     * Uses the same shared fragments and dashboard UI, but removes transient fragments with `RemoveAt`.
     * `RemoveAt` provides `OldItems`, so `GeometryGroup.OnChildrenCollectionChanged` unsubscribes each child geometry.
     * Counts should return close to the control run.

The app forces full GC before measurements so retained weak references are meaningful. It also reports managed heap, GC heap, resident memory, and working-set deltas after collection.

## Observed Results

Record unpatched local results here after running the autorun commands.
