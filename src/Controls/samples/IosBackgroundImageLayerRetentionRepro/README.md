# iOS Background Image Layer Retention Repro

This sample proves that iOS/Mac Catalyst background image updates leave assigned native `CALayer.Contents` image state on retained native view peers after handler disconnect. Each cycle loads a custom MAUI `ImageSource`, disconnects the `ContentViewHandler`, keeps the native `ContentView` alive, and counts MAUI background layers that still have contents.

The control run disposes the image-service result and removes the background layer before disconnect. The current run uses MAUI's `UIView.UpdateBackgroundImageSourceAsync` and normal `ContentViewHandler` disconnect behavior.

Run:

```sh
dotnet run --project src/Controls/samples/IosBackgroundImageLayerRetentionRepro/IosBackgroundImageLayerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.

Verified on Mac Catalyst:

```text
Control estimated assigned native background image payload: 0.0 MiB
Current estimated assigned native background image payload: 120.0 MiB
native peers with background layer contents: 120/120
RESULT: PROVEN
```
