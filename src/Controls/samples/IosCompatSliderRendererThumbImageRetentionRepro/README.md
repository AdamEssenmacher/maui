# iOS Compatibility SliderRenderer Thumb Image Retention Repro

This sample proves that legacy iOS/Mac Catalyst `SliderRenderer` leaves assigned native `UIImage` thumb state on retained `UISlider` peers after renderer disposal. Each cycle creates a compatibility-rendered `Slider` with a custom `ThumbImageSource`, disposes the renderer, keeps only the native `UISlider` peer alive, and counts assigned native thumb images after full GC.

The control run clears the native thumb image before disposal. The current run uses MAUI's existing `SliderRenderer.Dispose(bool)` path, which removes events and gestures but does not clear the `UISlider` thumb image assigned by `UpdateThumbImage`.

Run:

```sh
dotnet run --project src/Controls/samples/IosCompatSliderRendererThumbImageRetentionRepro/IosCompatSliderRendererThumbImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-compat-slider-renderer-thumb-image-retention-results.txt`.
