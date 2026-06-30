# GestureRecognizers Collection Retention Repro

This sample proves that the public `View.GestureRecognizers` and `Span.GestureRecognizers` collections can retain their discarded owners.

The repro creates discarded `View` and `Span` owners with realistic 1 MiB binding payloads. Each owner gets gesture recognizers added and then cleared so the retained collection is empty and does not retain the owner through recognizer `Parent` back-references. The app keeps only the public gesture collection references.

Current MAUI keeps the owners alive through the anonymous `CollectionChanged` handlers installed by `View` and `GestureElement` constructors. The control run reflectively clears the same collection event fields before retaining the empty collections.

Expected result:

```text
RESULT: PROVEN
control: 0/160 owners, payloads, and payload buffers retained
current: 160/160 owners, payloads, and payload buffers retained
```

Run:

```bash
dotnet build src/Controls/samples/GestureRecognizersCollectionRetentionRepro/GestureRecognizersCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/GestureRecognizersCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/GestureRecognizersCollectionRetentionRepro.app
cat /tmp/gesture-recognizers-collection-retention-results.txt
```
