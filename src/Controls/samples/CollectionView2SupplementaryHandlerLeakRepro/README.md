# CollectionView2SupplementaryHandlerLeakRepro

This repro demonstrates that current iOS/Mac Catalyst `TemplatedCell2` supplementary-view replacement can leave removed header/footer views attached to their old handlers.

`StructuredItemsViewController2.UpdateTemplatedSupplementaryView()` sets `TemplatedCell2.isHeaderOrFooterChanged` while rebinding a header or footer cell. In that replacement path, `TemplatedCell2.BindVirtualView()` clears the old binding context, removes the old view from the logical tree, nulls the cell's `PlatformHandler`, and removes the old platform view from its superview. It does not call `DisconnectHandler()` on the old view's handler. If app code retains removed header/footer views for reuse, those views keep their old handlers and handler payloads alive indefinitely.

The repro keeps removed supplementary views alive in both scenarios. The control explicitly disconnects the old supplementary handler before replacement. The suspect path follows the `TemplatedCell2` supplementary replacement cleanup without an explicit old-handler disconnect. Each old handler owns a realistic 1 MiB offline header payload.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/CollectionView2SupplementaryHandlerLeakRepro/CollectionView2SupplementaryHandlerLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/CollectionView2SupplementaryHandlerLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/CollectionView2SupplementaryHandlerLeakRepro.app --args --auto-run --results=/tmp/collectionview2supplementaryhandlerleakrepro-results.txt
cat /tmp/collectionview2supplementaryhandlerleakrepro-results.txt
```

Expected result:

```text
CollectionView2 supplementary handler leak repro
Cycles: 80
Payload per removed supplementary handler: 1 MiB
Leak proved: True

Scenario: explicit old supplementary handler disconnect before replacement
  Tracked removed views: 80
  Retained removed views alive: 80/80
  Removed view handlers alive: 0/80
  Native payload views alive: 80/80
  Payloads alive: 0/80
  Retained payload bytes: 0 B (0.0%)

Scenario: TemplatedCell2 supplementary replacement without old-handler disconnect
  Tracked removed views: 80
  Retained removed views alive: 80/80
  Removed view handlers alive: 80/80
  Native payload views alive: 80/80
  Payloads alive: 80/80
  Retained payload bytes: 80.0 MiB (100.0%)
```
