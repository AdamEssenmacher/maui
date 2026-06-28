# ContextActionsCellGlobalCloserRetentionRepro

This sample proves that iOS/Mac Catalyst compatibility `ContextActionsCell` can retain disposed opened context-action cells through native closer recognizers.

When a context-action row is opened, `ContextScrollViewDelegate.WillEndDragging()` adds a `GlobalCloseContextGestureRecognizer` to the live `UITableView` and a tap closer to the row content cell. `ContextActionsCell.Dispose()` clears and disposes the scroller, but it does not call `ContextScrollViewDelegate.Unhook()` first. If the table remains alive, the global closer keeps its close-action closure alive, which keeps the disposed context-action cell, its disposed row scroller, its disposed native payload cell, and row-scoped payload state alive.

The autorun keeps the stale global closer recognizers alive directly after creation. That isolates the table-owned native root without letting `UITableView` reuse state mask the control scenario. The control path calls `ContextScrollViewDelegate.Unhook()` before disposal and releases all row payloads.

Run:

```sh
dotnet build src/Controls/samples/ContextActionsCellGlobalCloserRetentionRepro/ContextActionsCellGlobalCloserRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ContextActionsCellGlobalCloserRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ContextActionsCellGlobalCloserRetentionRepro.app --args --auto-run --results=/tmp/contextactionscellglobalcloserretentionrepro-results.txt
cat /tmp/contextactionscellglobalcloserretentionrepro-results.txt
```
