# BackgroundImageSourceResultDisposeLeakRepro

This repro demonstrates that `UIView.UpdateBackgroundImageSourceAsync` can leak disposable native image results because it awaits `IImageSourceService.GetImageAsync` directly and never disposes the returned `IImageSourceServiceResult`.

The simulated background-image path matches page, view, entry, editor, and Shell flyout background-image usage:

1. A background image source is resolved through `IImageSourceService`.
2. The returned `UIImage` is applied to a `CALayer`.
3. The `IImageSourceServiceResult` is dropped without `Dispose()`, so native/service resources owned by the result are never released.

The control path uses the same image-service call and layer application, but disposes the result in a `finally` block.

Run with:

```bash
dotnet build src/Controls/samples/BackgroundImageSourceResultDisposeLeakRepro/BackgroundImageSourceResultDisposeLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/BackgroundImageSourceResultDisposeLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/BackgroundImageSourceResultDisposeLeakRepro.app --args --auto-run --results=/tmp/backgroundimagesourceresultdisposeleakrepro-results.txt
cat /tmp/backgroundimagesourceresultdisposeleakrepro-results.txt
```
