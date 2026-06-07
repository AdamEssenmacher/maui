# ShapeCollectionResetLeakRepro

This repro targets MAUI shape collection reset paths where a parent shape object subscribes to child drawing objects and later calls `Clear()` on an `ObservableCollection<T>`. `Clear()` raises a reset notification without `OldItems`, so handlers that only unsubscribe `OldItems` leave removed shared children subscribed.

The primary target is the untracked `PathFigure.Segments.Clear()` leak. The app also includes related reset leaks for comparison. `GeometryGroup.Children.Clear()` is the known case tracked by dotnet/maui#35795.

## Retention Paths

`PathFigure.Segments.Clear()`:

    shared PathSegment
      -> PropertyChanged delegate
      -> page-local PathFigure
      -> InvalidatePathSegmentRequested delegate
      -> page-local PathGeometry
      -> InvalidatePathGeometryRequested delegate from Path.Data
      -> Path
      -> dashboard page and BindingContext payload

`PathGeometry.Figures.Clear()`:

    shared PathFigure
      -> PropertyChanged / InvalidatePathSegmentRequested delegate
      -> page-local PathGeometry
      -> InvalidatePathGeometryRequested delegate from Path.Data
      -> Path
      -> dashboard page and BindingContext payload

Known related issue, `GeometryGroup.Children.Clear()`:

    shared Geometry fragment
      -> PropertyChanged delegate
      -> page-local GeometryGroup
      -> PropertyChanged delegate from Path.Data
      -> Path
      -> dashboard page and BindingContext payload

The sample uses realistic dashboard pages with 24 case cards per page, app-level shared vector fragments, and cached case-file payloads to show the practical impact.

## Run

Build from the repo root:

    dotnet build Microsoft.Maui.BuildTasks.slnf
    dotnet build src/Controls/samples/ShapeCollectionResetLeakRepro/ShapeCollectionResetLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
    dotnet build src/Controls/samples/ShapeCollectionResetLeakRepro/ShapeCollectionResetLeakRepro.csproj -f net10.0-android
    dotnet build src/Controls/samples/ShapeCollectionResetLeakRepro/ShapeCollectionResetLeakRepro.csproj -f net10.0-maccatalyst

Run the primary proof on an iOS simulator. Replace `UDID` with an available simulator from `xcrun simctl list devices available`:

    UDID=6E03E75B-32BB-4A97-BFB8-24E3307573E4
    xcrun simctl boot "$UDID"
    xcrun simctl install "$UDID" artifacts/bin/ShapeCollectionResetLeakRepro/Debug/net10.0-ios/iossimulator-arm64/ShapeCollectionResetLeakRepro.app
    xcrun simctl launch --terminate-running-process "$UDID" com.microsoft.maui.shapecollectionresetleakrepro --auto-run --target=PathFigureSegments
    APP_CONTAINER=$(xcrun simctl get_app_container "$UDID" com.microsoft.maui.shapecollectionresetleakrepro data)
    cat "$APP_CONTAINER/Library/ShapeCollectionResetLeakRepro/autorun-results.txt"

Run Mac Catalyst locally:

    dotnet run --project src/Controls/samples/ShapeCollectionResetLeakRepro/ShapeCollectionResetLeakRepro.csproj -f net10.0-maccatalyst

Run Android autorun:

    dotnet run --project src/Controls/samples/ShapeCollectionResetLeakRepro/ShapeCollectionResetLeakRepro.csproj -f net10.0-android -p:ShapeCollectionResetLeakReproAutoRun=true
    adb shell run-as com.microsoft.maui.shapecollectionresetleakrepro find . -name autorun-results.txt -print -exec cat {} \;

Targets can be selected in the UI or with `--target=` / `SHAPE_COLLECTION_RESET_LEAK_REPRO_TARGET`:

  * `PathFigureSegments`
  * `PathGeometryFigures`
  * `GeometryGroupChildrenKnownIssue`

## What to Check

Use the default settings first:

  * Pages/run: `50`
  * Payload MB/page: `4`
  * Cards/page: `24`
  * Shared items/card: `6`
  * Dwell ms/page: `50`

Run these scenarios for the selected target:

  1. `Run control`

     * Builds the same dashboard UI and still calls the target `Clear()`.
     * All transient drawing items are page-local, so the stale event cycle has no long-lived root.
     * After full GC, alive pages, payloads, paths, and tracked target owners should stay near zero.

  2. `Run shared Clear`

     * Seeds every page-local target owner with shared app-level drawing items, calls the target `Clear()`, and then rebuilds visible page-local drawing content.
     * On an unpatched build, the shared fragments retain the cleared owner objects, their `Path.Data` subscribers, the dashboard pages, and the cached payload view models.
     * With defaults, an unpatched build can retain about `200 MB` of view-model payload for one target, plus retained pages, layouts, paths, target owners, and native handlers.

  3. `Run mitigation`

     * Uses the same shared drawing items and dashboard UI, but removes transient items with `RemoveAt`.
     * `RemoveAt` provides `OldItems`, so the target collection change handler unsubscribes each child item.
     * Counts should return close to the control run.

The app forces full GC before measurements so retained weak references are meaningful. It also reports managed heap, GC heap, resident memory, and working-set deltas after collection.

## Observed Results

Observed on an unpatched local build with an iPhone 17 iOS 26.4 simulator (`6E03E75B-32BB-4A97-BFB8-24E3307573E4`) using the default `PathFigureSegments` autorun profile:

    ShapeCollectionResetLeakRepro autorun started at 2026-06-07T17:57:23.5591770-04:00
    Target: PathFigure.Segments.Clear
    Defaults: pages=50, payloadMB=4, cards=24, sharedItemsPerCard=6, dwellMs=50

    Run: control: fresh page-local PathSegments
    Pages pushed and popped: 50 in 00:36
    Tracked Path/PathFigures pairs: 1200
    Weak refs still alive after full GC:
      pages: 0/50
      payload view models: 0/50
      Paths: 0/1200
      PathFigures: 0/1200
    Payload retained by alive view models: 0 B (0.0% of allocated payload)
    Managed heap delta after GC: 2.8 MB
    GC heap delta after GC: 3.5 MB
    Resident memory delta: -170.7 MB

    Run: leaky shared PathSegments via Segments.Clear()
    Pages pushed and popped: 50 in 00:47
    Tracked Path/PathFigures pairs: 1200
    Weak refs still alive after full GC:
      pages: 0/50
      payload view models: 50/50
      Paths: 1200/1200
      PathFigures: 1200/1200
    Payload retained by alive view models: 200.0 MB (100.0% of allocated payload)
    Managed heap delta after GC: 227.3 MB
    GC heap delta after GC: 232.1 MB
    Resident memory delta: 76.8 MB

    Run: mitigation: remove shared PathSegments individually
    Pages pushed and popped: 50 in 01:00
    Tracked Path/PathFigures pairs: 1200
    Weak refs still alive after full GC:
      pages: 0/50
      payload view models: 0/50
      Paths: 0/1200
      PathFigures: 0/1200
    Payload retained by alive view models: 0 B (0.0% of allocated payload)
    Managed heap delta after GC: 157.9 KB
    GC heap delta after GC: 1.9 MB
    Resident memory delta: 23.1 MB
