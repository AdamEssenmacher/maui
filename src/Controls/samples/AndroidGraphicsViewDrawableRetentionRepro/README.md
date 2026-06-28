# Android GraphicsView Drawable Retention Repro

This sample proves that Android `GraphicsViewHandler` disconnect leaves native drawable state assigned on retained `PlatformTouchGraphicsView` peers. `PlatformTouchGraphicsView.Disconnect()` clears its `IGraphicsView` interaction reference, but inherited `PlatformGraphicsView.Drawable` remains assigned.

The repro keeps 80 native peers alive in both scenarios, then compares current disconnect against an explicit `platformView.Drawable = null` control. Each drawable carries a 1 MiB payload to model real dashboard, chart, or canvas drawables with cached drawing state.

Run:

```sh
dotnet build src/Controls/samples/AndroidGraphicsViewDrawableRetentionRepro/AndroidGraphicsViewDrawableRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
```

Install and launch the signed APK, then read the app-private `files/autorun-results.txt`.

Verified on `Maui_Tiny_API33` with about 984 MB guest RAM:

```text
Control retained payload: 0.0 MiB
Current retained payload: 80.0 MiB
RESULT: PROVEN
```
