# CollectionViewTemplatedCellHandlerLeakRepro

This repro demonstrates that the older iOS/Mac Catalyst `CollectionViewHandler` `TemplatedCell` item-template replacement path can leave removed item views attached to their old handlers.

`TemplatedCell.Bind()` removes the previous item view from the logical tree and removes its platform subview when the selected `DataTemplate` changes. It then creates a new view and handler for the new template. The replacement path does not call `DisconnectHandler()` on the old view's handler. If app code retains removed item views for reuse or diagnostics, those views keep their old handlers and handler payloads alive indefinitely.

The default iOS/Mac Catalyst `CollectionView` handler is now `CollectionViewHandler2`; this repro targets the still-shipped older handler stack that can be explicitly registered. Each old item handler owns a realistic 1 MiB offline row payload.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/CollectionViewTemplatedCellHandlerLeakRepro/CollectionViewTemplatedCellHandlerLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/CollectionViewTemplatedCellHandlerLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/CollectionViewTemplatedCellHandlerLeakRepro.app --args --auto-run --results=/tmp/collectionviewtemplatedcellhandlerleakrepro-results.txt
cat /tmp/collectionviewtemplatedcellhandlerleakrepro-results.txt
```

Expected result:

```text
CollectionView TemplatedCell handler leak repro
Cycles: 80
Payload per removed item handler: 1 MiB
Leak proved: True

Scenario: explicit old item handler disconnect before template replacement
  Tracked removed item views: 80
  Retained removed item views alive: 80/80
  Removed item handlers alive: 0/80
  Native payload views alive: 80/80
  Payloads alive: 0/80
  Retained payload bytes: 0 B (0.0%)

Scenario: TemplatedCell item-template replacement without old-handler disconnect
  Tracked removed item views: 80
  Retained removed item views alive: 80/80
  Removed item handlers alive: 80/80
  Native payload views alive: 80/80
  Payloads alive: 80/80
  Retained payload bytes: 80.0 MiB (100.0%)
```
