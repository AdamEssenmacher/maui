# ListViewCleanupHeaderFooterLeakRepro

This repro demonstrates that current iOS/Mac Catalyst `ListViewRenderer.CleanUpResources()` drops header and footer child-handler references without first disconnecting those handlers.

`ListViewRenderer.DisconnectHandler()` and `Dispose()` both call `CleanUpResources()`. That cleanup disposes child modal handlers and clears `_headerRenderer` / `_footerRenderer`, but it does not call `DisconnectHandler()` on either child handler. If app code retains the `ListView` or its header/footer views for reuse, those virtual views can keep their header/footer handlers and handler payloads alive indefinitely.

The repro keeps `ListView` instances with header/footer views alive in both scenarios. The control explicitly disconnects both child handlers before parent cleanup. The suspect path lets `ListViewRenderer.CleanUpResources()` perform the parent cleanup. Each child handler owns a realistic 1 MiB offline header/footer payload.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/ListViewCleanupHeaderFooterLeakRepro/ListViewCleanupHeaderFooterLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ListViewCleanupHeaderFooterLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ListViewCleanupHeaderFooterLeakRepro.app --args --auto-run --results=/tmp/listviewcleanupheaderfooterleakrepro-results.txt
cat /tmp/listviewcleanupheaderfooterleakrepro-results.txt
```

Expected result:

```text
ListView cleanup header/footer disconnect leak repro
Cycles: 80
Payload per header/footer handler: 1 MiB
Leak proved: True

Scenario: explicit child-handler disconnect before ListView cleanup
  Tracked cycles: 160
  Retained virtual header/footer views alive: 160/160
  Header/footer handlers alive: 0/160
  Native payload views alive: 160/160
  Payloads alive: 0/160
  Retained payload bytes: 0 B (0.0%)

Scenario: ListViewRenderer.CleanUpResources without child-handler disconnect
  Tracked cycles: 160
  Retained virtual header/footer views alive: 160/160
  Header/footer handlers alive: 160/160
  Native payload views alive: 160/160
  Payloads alive: 160/160
  Retained payload bytes: 160.0 MiB (100.0%)
```
