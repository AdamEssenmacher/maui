# Page InternalChildren Clear Retention Repro

This repro demonstrates that `Page.InternalChildren.Clear()` can leave removed children in the page's logical-child list because `ObservableCollection<T>.Clear()` raises a `Reset` notification with no `OldItems`.

The control path removes each child with `InternalChildren.RemoveAt(...)`, which lets `Page.InternalChildrenOnCollectionChanged(...)` call `RemoveLogicalChild(...)`. The current path uses `InternalChildren.Clear()`, leaving the live page with no internal children but stale logical children that retain realistic child binding payloads.

Build:

```bash
dotnet build src/Controls/samples/PageInternalChildrenClearRetentionRepro/PageInternalChildrenClearRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -p:EnableMauiAssetProcessing=false -p:EnableMauiImageProcessing=false -p:EnableMauiSplashScreenProcessing=false -m:1 -nr:false -v:minimal -clp:Summary
```

Run:

```bash
open -W "artifacts/bin/PageInternalChildrenClearRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/Page InternalChildren Clear Retention.app" --args --results=/tmp/page-internalchildren-clear-retention-results.txt
```
