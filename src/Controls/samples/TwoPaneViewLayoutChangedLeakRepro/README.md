# TwoPaneView LayoutChanged Leak Repro

This repro proves that `TwoPaneView.OnHandlerChangingCore` subscribes to `IFoldableService.OnLayoutChanged` without a corresponding unsubscribe path.

The app uses a reflection-created implementation of MAUI's internal `IFoldableService`, then creates transient `TwoPaneView` instances through the internal constructor. The control keeps services alive but does not trigger handler-changing; the leak scenario invokes the same handler-changing path that subscribes to the retained service.

Run on Mac Catalyst:

```bash
dotnet build src/Controls/samples/TwoPaneViewLayoutChangedLeakRepro/TwoPaneViewLayoutChangedLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/TwoPaneViewLayoutChangedLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/TwoPaneViewLayoutChangedLeakRepro.app --args --auto-run --results=/tmp/twopaneviewlayoutchangedleakrepro-results.txt
cat /tmp/twopaneviewlayoutchangedleakrepro-results.txt
```
