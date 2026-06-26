# ShellFlyoutContentRendererLeakRepro

This Mac Catalyst repro exercises the Shell flyout content renderer disposal path.

`ShellFlyoutContentRenderer` subscribes to `Shell.PropertyChanged` in its constructor. It also creates a `ShellTableViewController` that subscribes to Shell flyout item changes. The content renderer has no disposal override to remove its Shell event subscription or dispose the table controller.

The repro retains realistic live `Shell` roots in both scenarios:

- Control: explicitly remove the content renderer Shell event and dispose the table controller before disposing the renderer.
- Leak: call the current `ShellFlyoutContentRenderer.Dispose()` behavior.

Each Shell context carries a 1 MiB realistic payload. A proved run retains all disposed content renderers, table controllers, Shell contexts, and payloads only in the current-dispose scenario.

Run:

```sh
dotnet build src/Controls/samples/ShellFlyoutContentRendererLeakRepro/ShellFlyoutContentRendererLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ShellFlyoutContentRendererLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellFlyoutContentRendererLeakRepro.app --args --auto-run --results=/tmp/shellflyoutcontentrendererleakrepro-results.txt
cat /tmp/shellflyoutcontentrendererleakrepro-results.txt
```
