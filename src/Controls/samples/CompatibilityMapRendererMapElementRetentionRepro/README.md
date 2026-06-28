# Compatibility MapRenderer MapElement Retention Repro

This sample proves that the iOS/Mac Catalyst compatibility `MapRenderer` can retain disposed renderer state through `MapElement.PropertyChanged` subscriptions. `MapRenderer.Dispose(bool)` removes the `MapElements.CollectionChanged` handler but does not detach each tracked map element or clear `_trackedMapElements`.

The repro intentionally retains one `MapElement` per cycle, detaches the core `Map` event to avoid overlapping with the shared `Map.MapElements.Clear()` leak class, and then compares current disposal against an explicit renderer cleanup control. Current MAUI keeps each disposed renderer alive from the retained sentinel element, and the renderer keeps sibling map elements and payloads alive through `_trackedMapElements`.

Run:

```sh
dotnet run --project src/Controls/samples/CompatibilityMapRendererMapElementRetentionRepro/CompatibilityMapRendererMapElementRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.
