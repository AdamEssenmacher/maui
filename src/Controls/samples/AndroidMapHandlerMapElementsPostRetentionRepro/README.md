# Android MapHandler MapElements post retention repro

This repro checks whether detached Android `MapView` peers keep `MapHandler`, its `IMap`, and its `MauiContext` alive through the deferred callback scheduled by `MapHandler.MapElements()`.

It compares current MAUI against a control run that creates and disconnects the same `MapHandler`/`MapView` shape, but skips the `MapElements` update that queues the handler-capturing `MapView.Post(...)` callback. Both runs use a test `MapHandler` subclass that skips `GetMapAsync`, so the proof is distinct from the pending `GetMapAsync` callback leak. The sample autoruns on launch, writes `autorun-results.txt`, and exits.

Build:

```bash
dotnet build src/Controls/samples/AndroidMapHandlerMapElementsPostRetentionRepro/AndroidMapHandlerMapElementsPostRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

Run:

```bash
adb install --no-incremental -r artifacts/bin/AndroidMapHandlerMapElementsPostRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidmaphandlermapelementspostretentionrepro-Signed.apk
adb shell am start -S -n com.microsoft.maui.androidmaphandlermapelementspostretentionrepro/crc64a8fcb6782075e4d2.MainActivity
adb shell run-as com.microsoft.maui.androidmaphandlermapelementspostretentionrepro cat files/autorun-results.txt
```
