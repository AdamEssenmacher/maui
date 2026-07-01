# IosMapElementRemovedHandlerRetentionRepro

This repro targets the iOS/Mac Catalyst `MapHandler` map-element rebuild path
where removed `MapElement` instances can keep their internal
`MapElementHandler` and `MauiContext` after removal from `Map.MapElements`.

The app intentionally keeps removed `Polyline` models alive in a long-lived
list. That matches route, track, cache, clustering, and view-model overlay
collections. The leak is that `MauiMKMapView.ClearMapElements()` clears
`MapElementId` for removed elements but does not disconnect or clear the
handler stored on map elements whose overlay renderer was already requested by
MapKit.

The control removes elements one by one, then explicitly disconnects and clears
the removed element handler. The current MAUI run removes elements one by one as
well, avoiding the already-known `MapElements.Clear()` reset subscription leak,
but leaves the removed element handler assigned.

## Run

```bash
dotnet build src/Controls/samples/IosMapElementRemovedHandlerRetentionRepro/IosMapElementRemovedHandlerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true
open -W artifacts/bin/IosMapElementRemovedHandlerRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosMapElementRemovedHandlerRetentionRepro.app
cat /tmp/ios-mapelement-removed-handler-retention-results.txt
```

The harness auto-runs on launch and exits with code `0` when the leak is
proved.

Observed Mac Catalyst proof on 2026-07-01 retained 80 removed `Polyline`
models in both scenarios. The explicit cleanup control retained `0/80` removed
map element handlers, `0/80` `MauiContext` graphs, and `0 B` of context
payload. Current MAUI retained `80/80` removed map element handlers, `80/80`
`MauiContext` graphs, and `80.0 MiB` of context payload while `Map`s and
`MapHandler`s collected. `MapElementId` was cleared in both runs, isolating the
stale handler as the root.
