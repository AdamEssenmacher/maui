# Compatibility MapRenderer Reset Retention Repro

This sample proves that the iOS/Mac Catalyst compatibility `MapRenderer` can retain disposed renderer state after `Map.MapElements.Clear()`. The reset path clears `_trackedMapElements`, but it does not detach each removed `MapElement.PropertyChanged` handler. Later disposal sees an empty `MapElements` collection and cannot remove the stale subscriptions.

The repro intentionally retains one removed map element per cycle, detaches the core `Map` event to avoid overlapping with the shared `Map.MapElements.Clear()` leak class, and gives each renderer a realistic window/page-scoped `MauiContext` payload. Current MAUI keeps each disposed renderer alive from the retained removed element, and the renderer keeps its old `MauiContext` service payload alive.

Run:

```sh
dotnet run --project src/Controls/samples/CompatibilityMapRendererResetRetentionRepro/CompatibilityMapRendererResetRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.
