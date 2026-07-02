# LegacyTableViewModelRendererSourceRetentionRepro

This repro demonstrates that obsolete iOS/Mac Catalyst `Microsoft.Maui.Controls.Compatibility.Platform.iOS.TableViewModelRenderer` instances can remain rooted by a long-lived `TableView.ModelChanged` subscription.

`src/Compatibility/Core/src/iOS/Renderers/TableViewRenderer.cs` creates a new obsolete `TableViewModelRenderer` whenever the native source is refreshed. The source subscribes to `TableView.ModelChanged` with an anonymous handler and has no detach path. If a stale source has cached realistic section header cells, the stale source, headers, and payloads remain alive after the active `TableView.Model` is replaced.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/LegacyTableViewModelRendererSourceRetentionRepro/LegacyTableViewModelRendererSourceRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true
open -W "artifacts/bin/LegacyTableViewModelRendererSourceRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/Legacy TableViewModelRenderer Source Retention.app" --args --auto-run --results=/tmp/legacy-tableviewmodelrenderer-source-retention-results.txt
cat /tmp/legacy-tableviewmodelrenderer-source-retention-results.txt
```

Expected result:

```text
Run: control: legacy sources cached headers but ModelChanged subscriptions were cleared
  headers alive after full GC: 0/80
  payloads alive after full GC: 0/80
  native sources alive after full GC: 0/80

Run: current: obsolete TableViewModelRenderer remains subscribed to ModelChanged
  headers alive after full GC: 80/80
  payloads alive after full GC: 80/80
  native sources alive after full GC: 80/80
  retained payload bytes: 80.0 MiB (100.0%)
```
