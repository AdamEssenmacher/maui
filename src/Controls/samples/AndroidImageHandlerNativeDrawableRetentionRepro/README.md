# Android Image Handler Native Drawable Retention Repro

This sample proves that Android `ImageHandler` and `ImageButtonHandler` disconnect leave their last loaded native `Drawable` assigned on retained native `ImageView` peers. Each cycle loads a custom MAUI `ImageSource` through the normal `ImageSourcePartLoader`, then disconnects the handler while retaining the Android view. The same run also checks `ButtonHandler`; it does not retain the original payload drawable in this repro.

The control run explicitly clears the native drawable or icon before disconnect. The current run uses MAUI's disconnect behavior. Each drawable carries a 1 MiB payload to model real feeds, avatar grids, message previews, or dashboards with many decoded images.

Run:

```sh
dotnet build src/Controls/samples/AndroidImageHandlerNativeDrawableRetentionRepro/AndroidImageHandlerNativeDrawableRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
```

Install and launch the signed APK, then read the app-private `files/autorun-results.txt`.

Verified on a low-RAM emulator with about 984 MB guest RAM:

```text
Control retained payload: 0.0 MiB
Current retained payload: 160.0 MiB
Image: payload=80/80
ImageButton: payload=80/80
Button: payload=0/80
RESULT: PROVEN
```
