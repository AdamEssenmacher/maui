# TableViewCellParentRetentionLeakRepro

This repro proves that removing a cell from a `TableSection` can leave the removed `Cell` rooted by the still-live `TableView`.

`TableView` sets each cell's `Parent` to itself in `Root` setup and `OnModelChanged()`. `OnSectionCollectionChanged()` only assigns parents for new cells, and `TableSectionBase<T>.Remove()`, `RemoveAt()`, and `Clear()` do not clear the removed cell's parent. Setting `Cell.Parent` subscribes the cell to parent property/resource notifications, so a long-lived `TableView` can keep removed cells and their binding-context payloads alive after row churn.

Run:

```sh
dotnet build src/Controls/samples/TableViewCellParentRetentionLeakRepro/TableViewCellParentRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/TableViewCellParentRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/TableViewCellParentRetentionLeakRepro.app --args --results=/tmp/tableviewcellparentretentionleakrepro-results.txt
cat /tmp/tableviewcellparentretentionleakrepro-results.txt
```
