# Android Legacy Page Background Drawable Retention Repro

This sample proves that obsolete Android compatibility `PageRenderer` disposal leaves an assigned `BackgroundImageSource` drawable on retained native renderer peers.

The repro exercises the real legacy renderer path:

- `PageRenderer.UpdateBackground(...)` loads `Page.BackgroundImageSource` through the legacy `IImageSourceHandler`/`ApplyDrawableAsync` path.
- The update callback assigns the generated payload image through `View.SetBackground(drawable)`.
- `PageRenderer.Dispose(bool)` sends disappearing and delegates to base disposal, but it does not clear the native background slot.

The control run clears only the native background before renderer disposal. The current run uses MAUI's disposal behavior. Both runs retain the disposed native `PageRenderer` peers through JNI global references so the managed renderer, page, and source graphs can collect independently.

Run:

```sh
dotnet build src/Controls/samples/AndroidLegacyPageBackgroundDrawableRetentionRepro/AndroidLegacyPageBackgroundDrawableRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
```

Install and launch the signed APK, then read the app-private `files/autorun-results.txt`.

Expected proof shape on the low-RAM Android emulator:

```text
Control retained background payload: 0 B
Current retained background payload: 96.0 MiB
payload-sized native background slots: 96/96
RESULT: PROVEN
```
