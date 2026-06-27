# TableView Source Gesture Retention Repro

This sample proves that iOS/Mac Catalyst compatibility `TableViewModelRenderer` instances can remain rooted by native gesture recognizers after the table source is replaced.

`TableViewModelRenderer.BindGestures()` adds a `UILongPressGestureRecognizer` and `UITapGestureRecognizer` to the native `UITableView`. Each recognizer targets methods on the source, but the source has no dispose path that removes those recognizers. When the table source is replaced, the old source can remain alive through the native table and keep cached header cells and their payload graphs in `_headerCells`.

The autorun creates 80 replacement sources with 1 MiB header payloads each. The control path caches the same header payloads without binding native gestures. The current MAUI path calls the normal source path that binds two recognizers per source and leaves them attached to the live native table.

Run:

```sh
dotnet run --project src/Controls/samples/TableViewSourceGestureRetentionRepro/TableViewSourceGestureRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The result file is written to the process temp directory as `tableview-source-gesture-retention-results.txt`.
