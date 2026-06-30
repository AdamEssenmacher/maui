# Android Legacy Image/Slider Drawable Retention Repro

This sample proves that obsolete Android compatibility `ImageRenderer` and `SliderRenderer` disposal leaves payload native drawable state assigned on retained native child peers.

The repro exercises the real legacy renderer paths:

- `ImageRenderer` loads a custom `ImageSource` through the legacy `IImageViewHandler` path and assigns `ImageView.SetImageDrawable(...)`.
- `SliderRenderer` loads the same custom source through the legacy `IImageSourceHandler`/`ApplyDrawableAsync` path and assigns `SeekBar.SetThumb(...)`.

The control run clears only the native image/slider slots before renderer disposal. The current run uses MAUI's disposal behavior. Both runs retain the disposed native child peers through JNI global references so the managed renderer, virtual view, and source graphs can collect independently.

Run:

```sh
dotnet build src/Controls/samples/AndroidLegacyImageSliderDrawableRetentionRepro/AndroidLegacyImageSliderDrawableRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
```

Install and launch the signed APK, then read the app-private `files/autorun-results.txt`.

Expected proof shape on the low-RAM Android emulator:

```text
Control retained drawable/thumb payload: 0 B
Current retained drawable/thumb payload: 192.0 MiB
ImageRenderer: payloadSlots=96/96, imagePayloads=96/96
SliderRenderer: payloadSlots=96/96
RESULT: PROVEN
```
