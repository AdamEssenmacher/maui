# ShapeBrushCollectionHandlesRetentionRepro

This repro targets owner-created public collection handles on brush, geometry, and transform objects:

- `GradientBrush.GradientStops`
- `PathFigure.Segments`
- `PathGeometry.Figures`
- `GeometryGroup.Children`
- `TransformGroup.Children`

Each owner creates its default collection and subscribes that collection's `CollectionChanged` event back to an instance method. If app code keeps the collection handle in a cache, service, view model, or pending operation after the owner is discarded, the collection keeps the owner alive through the event delegate.

The sample adds realistic child items, removes them one by one, and then retains only the now-empty collection handle. This avoids the already tracked shared-child and `Clear()` reset leak classes. The control run keeps the same collection handles but clears the collection event field by reflection before retaining them.

## Run

Build from the repo root:

```bash
dotnet build src/Controls/samples/ShapeBrushCollectionHandlesRetentionRepro/ShapeBrushCollectionHandlesRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

Run the Mac Catalyst autorun and write the result file:

```bash
open -W artifacts/bin/ShapeBrushCollectionHandlesRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShapeBrushCollectionHandlesRetentionRepro.app --args --results=/tmp/shape-brush-collection-handles-retention-results.txt
```

## Expected Result

The control run should retain zero owners and payloads after full GC. The current run should retain every discarded owner and its attached 1 MiB payload buffer through the app-retained collection handles.
