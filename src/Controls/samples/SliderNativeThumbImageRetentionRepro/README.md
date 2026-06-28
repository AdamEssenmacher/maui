# Slider Native Thumb Image Retention Repro

This sample proves that iOS/Mac Catalyst `SliderHandler` leaves assigned native `UIImage` thumb state on retained `UISlider` peers after handler disconnect. Each cycle loads a custom MAUI `ImageSource`, disconnects the handler, keeps the native `UISlider` alive, and counts assigned native thumb images.

The control run explicitly disposes the thumb image-service result and clears the native thumb image before disconnect. The current run uses MAUI's `UISlider.UpdateThumbImageSourceAsync` plus `SliderHandler.DisconnectHandler`.

Run:

```sh
dotnet run --project src/Controls/samples/SliderNativeThumbImageRetentionRepro/SliderNativeThumbImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.

Verified on Mac Catalyst:

```text
Control estimated assigned native thumb image payload: 0.0 MiB
Current estimated assigned native thumb image payload: 60.0 MiB
native peers with assigned thumb UIImages: 240/240
RESULT: PROVEN
```
