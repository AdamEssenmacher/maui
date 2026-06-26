# SwipeViewRenderer Content Leak Repro

This sample demonstrates a legacy iOS/Mac Catalyst compatibility renderer leak in `SwipeViewRenderer`.

`SwipeViewRenderer.UpdateContent()` subscribes to `Element.Content.PropertyChanged` whenever `SwipeView.Content` changes, but it never detaches the previous content. `Dispose()` only detaches the current content. If an app keeps replaced row content in a cache or reuse pool, the old content can retain the disposed renderer. The disposed renderer has already cleared `Element`, but it still keeps `_scrollParent`, so it can retain the parent `ScrollView` graph and its binding context.

The autorun uses 80 rows with 1 MiB payloads on the parent scroll containers. The control replaces content and keeps the old content cached without ever attaching the compatibility renderer; retained payloads should fall to zero, and the cached old content should no longer be parented. The suspect run performs the same content replacement after `SwipeViewRenderer` subscribes to the old content; the stale content event retains the disposed renderer and the parent scroll payloads.

Run from the repo root:

```bash
dotnet build src/Controls/samples/SwipeViewRendererContentLeakRepro/SwipeViewRendererContentLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/SwipeViewRendererContentLeakRepro/Debug_net10.0-maccatalyst/net10.0-maccatalyst/maccatalyst-arm64/SwipeViewRendererContentLeakRepro.app --args --auto-run --results=/tmp/swipeviewrenderercontentleakrepro-results.txt
```
