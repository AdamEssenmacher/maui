# TableViewSourceLeakRepro

This repro demonstrates that stale iOS/Mac Catalyst `TableViewModelRenderer` instances can remain rooted by a long-lived `TableView.ModelChanged` subscription.

`TableViewRenderer.SetSource()` creates a new `TableViewModelRenderer` whenever the native table source is refreshed, such as when `HasUnevenRows` changes. `TableViewModelRenderer` subscribes to `model.ModelChanged` with an anonymous handler and has no detach path. If a stale source has cached section header cells, those cells and their payloads remain alive after the active `TableView.Model` is replaced.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/TableViewSourceLeakRepro/TableViewSourceLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/TableViewSourceLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/TableViewSourceLeakRepro.app --args --auto-run --results=/tmp/tableviewsourceleakrepro-results.txt
cat /tmp/tableviewsourceleakrepro-results.txt
```

Expected result:

```text
Run: control: replace TableView model without native source
  headers alive after full GC: 0/80
  payloads alive after full GC: 0/80

Run: leak: stale TableViewModelRenderer subscribed to ModelChanged
  headers alive after full GC: 80/80
  payloads alive after full GC: 80/80
  native sources alive after full GC: 80/80
  retained payload bytes: 80.0 MiB (100.0%)
```
