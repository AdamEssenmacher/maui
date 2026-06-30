# Android EmptyViewAdapter Cleared EmptyView Retention Repro

This repro proves that the current Android `MauiRecyclerView` hidden `EmptyViewAdapter` keeps a stale `EmptyView` value after the public `CollectionView.EmptyView` property is cleared.

The app creates 80 live current-handler `CollectionView` instances, each with a 512 KiB payload object assigned to `EmptyView`. It forces the hidden empty adapter to cache that value, clears the public property, and retains only the native RecyclerView peers. The control run explicitly clears the hidden adapter's cached value after the public clear. The current MAUI run leaves `MauiRecyclerView.UpdateEmptyView()` behavior unchanged.

Run:

```bash
dotnet build src/Controls/samples/AndroidEmptyViewAdapterClearedValuesRetentionRepro/AndroidEmptyViewAdapterClearedValuesRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r artifacts/bin/AndroidEmptyViewAdapterClearedValuesRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidemptyviewadapterclearedvaluesretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidemptyviewadapterclearedvaluesretentionrepro/crc646c2aadeace61d215.MainActivity
adb shell run-as com.microsoft.maui.androidemptyviewadapterclearedvaluesretentionrepro cat files/autorun-results.txt
```
