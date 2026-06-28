# iOS Image Handler Native Image Retention Repro

This sample proves that iOS/Mac Catalyst `ImageHandler`, `ImageButtonHandler`, and `ButtonHandler` leave assigned native `UIImage` state on retained native peers after handler disconnect. Each cycle loads a custom MAUI `ImageSource`, disconnects the handler, keeps the native peer alive, and counts assigned native images.

The control run explicitly clears each native image slot and resets the relevant `ImageSourcePartLoader` before disconnect. The current run uses MAUI's disconnect behavior.

Run:

```sh
dotnet run --project src/Controls/samples/IosImageHandlerNativeImageRetentionRepro/IosImageHandlerNativeImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.

Verified on Mac Catalyst:

```text
Control estimated assigned native image payload: 0.0 MiB
Current estimated assigned native image payload: 240.0 MiB
Image: assignedImage=80/80
ImageButton: assignedImage=80/80
Button: assignedImage=80/80
RESULT: PROVEN
```
