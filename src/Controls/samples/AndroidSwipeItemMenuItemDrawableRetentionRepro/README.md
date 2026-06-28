# Android SwipeItemMenuItem Drawable Retention Repro

This sample checks whether Android `SwipeItemMenuItemHandler` disconnect leaves its loaded icon drawable assigned on the native `TextView` compound drawable slots. Each cycle loads a custom MAUI `ImageSource` through the normal `ImageSourcePartLoader`, disconnects the handler, and keeps the Android button peer alive.

The control run explicitly clears compound drawables and resets the source loader before disconnect. The current run uses MAUI's disconnect behavior. Each drawable carries a 1 MiB payload to model image-heavy swipe actions in inbox, feed, or file-list rows.

Run:

```sh
dotnet build src/Controls/samples/AndroidSwipeItemMenuItemDrawableRetentionRepro/AndroidSwipeItemMenuItemDrawableRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
```

Install and launch the signed APK, then read the app-private `files/autorun-results.txt`.

Verified on a low-RAM emulator with about 984 MB guest RAM:

```text
Control retained payload: 0.0 MiB
Current retained payload: 80.0 MiB
service results created/disposed: 80/0
alive Drawables: 80/80
alive payload byte arrays: 80/80
RESULT: PROVEN
```
