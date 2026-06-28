# CollectionView Header/Footer Dispose Leak Repro

This Mac Catalyst repro checks whether the older iOS `CollectionViewHandler` disconnect path disconnects active `Header` and `Footer` child handlers when the parent `CollectionView` is disconnected.

Run:

```sh
REPRO_RESULTS_PATH=/tmp/collectionview-headerfooter-dispose-leak-results.txt dotnet run --project src/Controls/samples/CollectionViewHeaderFooterDisposeLeakRepro/CollectionViewHeaderFooterDisposeLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```
