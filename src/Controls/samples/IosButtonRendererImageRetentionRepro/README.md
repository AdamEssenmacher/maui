# iOS ButtonRenderer Image Retention Repro

This sample proves that legacy iOS/Mac Catalyst `ButtonRenderer` can leave `Button.ImageSource` images assigned on retained native `UIButton` peers after renderer disposal. The repro loads realistic generated button images through the compatibility image-source path, retains only the native `UIButton`, and compares current disposal with a control run that clears `UIButton.SetImage(null, UIControlState.Normal)` before disposal.

Run:

```sh
dotnet run --project src/Controls/samples/IosButtonRendererImageRetentionRepro/IosButtonRendererImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-buttonrenderer-image-retention-results.txt`.
