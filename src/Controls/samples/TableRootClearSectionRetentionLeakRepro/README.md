# TableRootClearSectionRetentionLeakRepro

This repro proves that `TableRoot.Clear()` can leave removed `TableSection` objects subscribed to their old `TableRoot`.

`TableRoot.SetupEvents()` subscribes each added section to `ChildCollectionChanged` and `ChildPropertyChanged`, but it only detaches sections from `NotifyCollectionChangedEventArgs.OldItems`. `TableSectionBase<T>.Clear()` delegates to `ObservableCollection<T>.Clear()`, which raises a reset notification without old items. If app code retains removed sections for later reuse, those sections can keep the old `TableRoot`, `TableView`, and payload graph alive.

Run:

```sh
dotnet build src/Controls/samples/TableRootClearSectionRetentionLeakRepro/TableRootClearSectionRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/TableRootClearSectionRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/TableRootClearSectionRetentionLeakRepro.app --args --results=/tmp/tablerootclearsectionretentionleakrepro-results.txt
cat /tmp/tablerootclearsectionretentionleakrepro-results.txt
```
