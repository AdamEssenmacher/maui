# Map Public Collections Retention Repro

This sample proves that public `Map.Pins` and `Map.MapElements` collection handles can retain discarded `Map` instances.

The repro creates separate maps for the `Pins` and `MapElements` surfaces. Each map receives realistic pins or circle overlays, then removes them individually so the retained collection is empty and known reset-detach paths such as `MapElements.Clear()` do not explain the result. The app keeps only the public collection references.

Current MAUI keeps the maps alive through the `CollectionChanged` handlers installed by the `Map` constructor. The control run reflectively clears the same collection event fields before retaining the empty collections.

Expected result:

```text
RESULT: PROVEN
control: 0/160 maps, payloads, and payload buffers retained
current: 160/160 maps, payloads, and payload buffers retained
```

Run:

```bash
dotnet build src/Controls/samples/MapCollectionsRetentionRepro/MapCollectionsRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/MapCollectionsRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MapCollectionsRetentionRepro.app
cat /tmp/map-collections-retention-results.txt
```
