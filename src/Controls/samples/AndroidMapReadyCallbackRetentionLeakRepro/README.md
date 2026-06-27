# Android MapReady Callback Retention Leak Repro

This repro isolates the Android `MapHandler` pending `GetMapAsync` callback
path. `MapCallbackHandler` has a dispose path that clears its strong
`MapHandler` reference, but `MapHandler.DisconnectHandler()` only drops its
private `_mapReady` field. If the native `MapView` is still holding a pending
callback, that callback can retain the disconnected handler and its old
`MauiContext` service graph.

The app models the native pending callback queue directly so it does not need a
Google Maps API key.

Run:

```sh
dotnet build src/Controls/samples/AndroidMapReadyCallbackRetentionLeakRepro/AndroidMapReadyCallbackRetentionLeakRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
adb install -r artifacts/bin/AndroidMapReadyCallbackRetentionLeakRepro/Debug/net10.0-android/com.microsoft.maui.androidmapreadycallbackretentionleakrepro-Signed.apk
adb shell monkey -p com.microsoft.maui.androidmapreadycallbackretentionleakrepro 1
adb shell run-as com.microsoft.maui.androidmapreadycallbackretentionleakrepro cat files/autorun-results.txt
```
