# iOS Compatibility SwipeViewRenderer Image Retention Repro

This sample proves that legacy iOS/Mac Catalyst `SwipeViewRenderer` can leave swipe item state and swipe action icon images alive after renderer disposal. The repro creates vertical `SwipeItem` action buttons through the renderer's own swipe-item creation path, retains only the native action button, and compares current disposal with a control run that explicitly clears native button images and invokes the renderer's swipe-item cleanup before disposal.

Run:

```sh
dotnet run --project src/Controls/samples/IosCompatSwipeViewRendererImageRetentionRepro/IosCompatSwipeViewRendererImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-compat-swipeviewrenderer-image-retention-results.txt`.
