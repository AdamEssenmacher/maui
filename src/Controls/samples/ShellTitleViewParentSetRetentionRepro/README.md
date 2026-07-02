# Shell TitleView ParentSet Retention Repro

This repro proves an iOS/Mac Catalyst Shell title-view retention path in `ShellPageRendererTracker`.

When `ShellPageRendererTracker.UpdateTitleView()` sees a Shell title view whose `Parent` is still `null`, it subscribes to `titleView.ParentSet` and waits for the title view to be parented. If the tracker is disposed before that event fires, `Dispose()` does not remove the pending handler. A retained off-tree title view can then keep the disposed tracker alive.

The repro runs two scenarios:

- Control: explicitly removes the pending `ParentSet` handler before disposing the tracker.
- Current MAUI: disposes the tracker without removing the pending handler.

Each tracker is given an `IFontManager` service with a 1 MiB payload to model a realistic Shell/window service graph.

## Run

```bash
dotnet build src/Controls/samples/ShellTitleViewParentSetRetentionRepro/ShellTitleViewParentSetRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W artifacts/bin/ShellTitleViewParentSetRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellTitleViewParentSetRetentionRepro.app --args --results=/tmp/shell-titleview-parentset-retention-results.txt
cat /tmp/shell-titleview-parentset-retention-results.txt
```

## Proven Result

```text
Control: explicit pending ParentSet unsubscribe before disposing ShellPageRendererTracker
  Trackers alive: 0/96
  Font managers alive: 0/96
  Payload buffers alive: 0/96

Current MAUI: ShellPageRendererTracker.Dispose() does not unsubscribe the pending TitleView.ParentSet handler
  Trackers alive: 96/96
  Font managers alive: 96/96
  Payload buffers alive: 96/96
  Proven retained payload: 96.0 MiB
  Managed heap delta after both scenarios: 96.9 MiB

RESULT: PROVEN
```
