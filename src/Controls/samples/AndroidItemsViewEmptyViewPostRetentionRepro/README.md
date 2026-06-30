# Android ItemsView empty-view posted layout retention repro

This repro checks whether detached Android `RecyclerView` peers can keep `CollectionViewHandler` and its `MauiContext` alive through the deferred callback scheduled by `ItemsViewHandler.UpdateEmptyViewSize()`.

It compares current MAUI against a control run that creates and disconnects the same `CollectionView`/`RecyclerView` shape, but does not call the arrange path that posts the deferred empty-view layout callback. Both runs retain the native `RecyclerView` peers and explicitly clear the known stale `MauiRecyclerView` fields after disconnect, so retained scoped-service payloads are isolated to the posted callback. The sample autoruns on launch, writes `autorun-results.txt`, and exits.

Build:

```bash
dotnet build src/Controls/samples/AndroidItemsViewEmptyViewPostRetentionRepro/AndroidItemsViewEmptyViewPostRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

Run:

```bash
adb install --no-incremental -r artifacts/bin/AndroidItemsViewEmptyViewPostRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androiditemsviewemptyviewpostretentionrepro-Signed.apk
adb shell am start -S -n com.microsoft.maui.androiditemsviewemptyviewpostretentionrepro/crc64fd98b4eb595af0a7.MainActivity
adb shell run-as com.microsoft.maui.androiditemsviewemptyviewpostretentionrepro cat files/autorun-results.txt
```
