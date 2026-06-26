# ImageSourceResultChurnLeakRepro

This repro demonstrates that `ImageSourceServiceResultManager.CompleteLoad` can leak disposable native image results when overlapping image loads complete out of order.

The simulated source churn matches a common feed/carousel shape:

1. Slow source A starts.
2. Fast source B replaces it and applies.
3. Stale source A completes late.

`ImageSourcePartLoader.UpdateImageSourceAsync` calls `BeginLoad` for each source change and then calls `CompleteLoad(result)` when each awaited load returns. The current manager stores the late stale result and overwrites the applied result without disposing it. The next source change disposes the stale result, but the applied result is already lost.

Run with:

```bash
dotnet build src/Controls/samples/ImageSourceResultChurnLeakRepro/ImageSourceResultChurnLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W src/Controls/samples/ImageSourceResultChurnLeakRepro/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/ImageSourceResultChurnLeakRepro.app --args --auto-run --results=/tmp/imagesourceresultchurnleakrepro-results.txt
cat /tmp/imagesourceresultchurnleakrepro-results.txt
```
