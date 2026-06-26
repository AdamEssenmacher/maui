# ContextActionsCellPropertyChangedLeakRepro

This repro demonstrates that disposed iOS/Mac Catalyst `ContextActionsCell` instances can remain rooted by retained MAUI `Cell` instances.

`ContextActionsCell.Update()` subscribes to `cell.PropertyChanged` for non-recycling ListView cells. `Dispose()` removes the `ContextActions` collection subscription and clears `_cell`, but it does not remove `_cell.PropertyChanged -= OnCellPropertyChanged`. A retained MAUI cell can therefore keep the disposed native context-action wrapper alive. The wrapper keeps its `ContentCell` property, so the disposed native payload cell and its payload remain alive too.

The repro keeps the MAUI cells alive in both scenarios. The control scenario creates and disposes native payload cells without calling `ContextActionsCell.Update()`. The suspect scenario calls `Update()` and then disposes the context-action wrapper. Each disposed native payload cell carries a realistic 1 MiB offline-order payload.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/ContextActionsCellPropertyChangedLeakRepro/ContextActionsCellPropertyChangedLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ContextActionsCellPropertyChangedLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ContextActionsCellPropertyChangedLeakRepro.app --args --auto-run --results=/tmp/contextactionscellpropertychangedleakrepro-results.txt
cat /tmp/contextactionscellpropertychangedleakrepro-results.txt
```

Expected result:

```text
ContextActionsCell PropertyChanged leak repro
Cycles: 80
Payload per disposed native cell: 1 MiB
Leak proved: True

Scenario: retained MAUI cells with disposed native payload cells only
  Tracked cycles: 80
  Retained MAUI cells alive: 80/80
  Disposed ContextActionsCell instances alive: 0/80
  Disposed native payload cells alive: 0/80
  Payloads alive: 0/80
  Retained payload bytes: 0 B (0.0%)

Scenario: retained MAUI cells after disposed ContextActionsCell.Update
  Tracked cycles: 80
  Retained MAUI cells alive: 80/80
  Disposed ContextActionsCell instances alive: 80/80
  Disposed native payload cells alive: 80/80
  Payloads alive: 80/80
  Retained payload bytes: 80.0 MiB (100.0%)
```
