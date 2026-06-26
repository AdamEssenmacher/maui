# LoadImageResultDisposeLeakRepro

This repro demonstrates that `ImageSourceExtensions.LoadImage` can leak disposable native image results because it invokes callbacks with an `IImageSourceServiceResult` but does not dispose that result or return it to the caller for later cleanup.

The simulated callback shape matches legacy image-cell, Shell icon, toolbar icon, flyout background, and tab icon call sites:

1. An image source is resolved through `LoadImage`.
2. The callback reads `result.Value`.
3. The result falls out of scope without `Dispose()`, so native/service resources owned by the result are never released.

The control path uses `GetPlatformImageAsync`, invokes the same callback shape, and disposes the result in a `finally` block.

Run with:

```bash
dotnet build src/Controls/samples/LoadImageResultDisposeLeakRepro/LoadImageResultDisposeLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/LoadImageResultDisposeLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/LoadImageResultDisposeLeakRepro.app --args --auto-run --results=/tmp/loadimageresultdisposeleakrepro-results.txt
cat /tmp/loadimageresultdisposeleakrepro-results.txt
```
