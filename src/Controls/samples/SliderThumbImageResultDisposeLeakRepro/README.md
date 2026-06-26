# SliderThumbImageResultDisposeLeakRepro

This repro demonstrates that `UISlider.UpdateThumbImageSourceAsync` can leak disposable native image results because it awaits `IImageSourceService.GetImageAsync` directly and never disposes the returned `IImageSourceServiceResult`.

The simulated slider thumb-image path matches `Slider.ThumbImageSource` usage on iOS and Mac Catalyst:

1. A thumb `ImageSource` is resolved through `IImageSourceService`.
2. The returned `UIImage` is assigned to the native `UISlider` thumb image.
3. The `IImageSourceServiceResult` is dropped without `Dispose()`, so native/service resources owned by the result are never released.

The control path uses the same image-service call and native thumb assignment, but disposes the result in a `finally` block.

Run with:

```bash
dotnet build src/Controls/samples/SliderThumbImageResultDisposeLeakRepro/SliderThumbImageResultDisposeLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/SliderThumbImageResultDisposeLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/SliderThumbImageResultDisposeLeakRepro.app --args --auto-run --results=/tmp/sliderthumbimageresultdisposeleakrepro-results.txt
cat /tmp/sliderthumbimageresultdisposeleakrepro-results.txt
```
