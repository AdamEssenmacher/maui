# DualScreenInfo HandlerChanged Leak Repro

This repro proves that `DualScreenInfo(VisualElement)` can be retained by a long-lived visual element because its internal `TwoPaneViewLayoutGuide` subscribes to `VisualElement.HandlerChanged` without an unsubscribe path.

The control keeps visual elements alive but does not create `DualScreenInfo` observers. The leak scenario creates `DualScreenInfo` observers, attaches realistic `PropertyChanged` subscriber payloads, then drops the observers while retaining the elements.

Run on Mac Catalyst:

```bash
dotnet build src/Controls/samples/DualScreenInfoHandlerChangedLeakRepro/DualScreenInfoHandlerChangedLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/DualScreenInfoHandlerChangedLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/DualScreenInfoHandlerChangedLeakRepro.app --args --auto-run --results=/tmp/dualscreeninfohandlerchangedleakrepro-results.txt
cat /tmp/dualscreeninfohandlerchangedleakrepro-results.txt
```
