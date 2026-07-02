# VisualDiagnosticsOverlay ScrollViews Retention Repro

This sample proves a managed retention path in `VisualDiagnosticsOverlay.RemoveAdorners(IVisualTreeElement)`.

`VisualDiagnosticsOverlay.AddAdorner(...)` snapshots the current visual tree's `IScrollView` instances into the overlay's `_scrollViews` dictionary. On iOS and Mac Catalyst, each dictionary value is the KVO observer disposable created for the native `UIScrollView` `contentOffset` observer. `RemoveAdorner(IAdorner)`, `RemoveAdorners()`, and `Deinitialize()` clear this dictionary through `RemoveScrollableElementHandler()`, but `RemoveAdorners(IVisualTreeElement)` removes matching adorners directly through `base.RemoveWindowElement(...)` and never clears the scroll-handler dictionary when the last adorner is gone.

The repro repeats a realistic diagnostics flow 128 times:

- place a `ScrollView` with a 1 MiB payload in the live window;
- add a diagnostics adorner for that `ScrollView`;
- remove the adorner through `RemoveAdorners(IVisualTreeElement)`;
- remove the `ScrollView` from the visual tree.

The control scenario calls `RemoveScrollableElementHandler()` after the visual-specific removal. Current MAUI behavior does not. A proven run shows the control collecting the removed scroll views and payloads while current behavior keeps them alive through the live window's diagnostics overlay.

## Run

```bash
dotnet build src/Controls/samples/VisualDiagnosticsOverlayScrollViewsRetentionRepro/VisualDiagnosticsOverlayScrollViewsRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
open -W "artifacts/bin/VisualDiagnosticsOverlayScrollViewsRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/VisualDiagnosticsOverlay ScrollViews Retention.app" --args --results=/tmp/visualdiagnosticsoverlay-scrollviews-retention.txt
cat /tmp/visualdiagnosticsoverlay-scrollviews-retention.txt
```
