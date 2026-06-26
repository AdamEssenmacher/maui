# ListViewHeaderDisconnectLeakRepro

This repro demonstrates that current iOS/Mac Catalyst `ListViewRenderer.UpdateHeader()` leaves removed header handlers attached when `ListView.Header` is set to `null`.

`UpdateFooter()` handles the equivalent footer removal path by clearing the native footer, detaching `MeasureInvalidated`, disposing child handlers, calling `_footerRenderer.DisconnectHandler()`, and then clearing `_footerRenderer`. `UpdateHeader()` performs the same cleanup except it skips `_headerRenderer.DisconnectHandler()` in the `Header = null` branch. If app code retains removed header views for reuse, those virtual views can keep their child handlers and handler payloads alive indefinitely.

The repro keeps removed virtual header/footer views alive in both scenarios. The footer path is the control because MAUI disconnects the footer handler. The header path is the suspect because MAUI clears `_headerRenderer` without disconnecting it. Each child handler owns a realistic 1 MiB offline-header payload.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/ListViewHeaderDisconnectLeakRepro/ListViewHeaderDisconnectLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ListViewHeaderDisconnectLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ListViewHeaderDisconnectLeakRepro.app --args --auto-run --results=/tmp/listviewheaderdisconnectleakrepro-results.txt
cat /tmp/listviewheaderdisconnectleakrepro-results.txt
```

Expected result:

```text
ListView header disconnect leak repro
Cycles: 80
Payload per native header view: 1 MiB
Leak proved: True

Scenario: footer removed through ListViewRenderer.UpdateFooter
  Retained virtual header/footer views alive: 80/80
  Header/footer handlers alive: 0/80
  Payloads alive: 0/80
  Retained payload bytes: 0 B (0.0%)

Scenario: header removed through ListViewRenderer.UpdateHeader
  Retained virtual header/footer views alive: 80/80
  Header/footer handlers alive: 80/80
  Payloads alive: 80/80
  Retained payload bytes: 80.0 MiB (100.0%)
```
