# Android EmptyViewAdapter Header/Footer Holder Retention Repro

This repro exercises the Android `EmptyViewAdapter` path for plain Forms-view `CollectionView.Header` and `CollectionView.Footer` values while the empty adapter is active.

It retains only the native RecyclerView holder objects after header/footer removal and adapter recycle/dispose. The control run explicitly removes the logical child, recycles the `ItemContentView`, and clears holder references. The current MAUI run uses the framework cleanup path, where `EmptyViewAdapter.OnViewRecycled()` calls `SimpleViewHolder.Recycle()`, but that method only recycles `SizedItemContentView` and skips the plain `ItemContentView` used for header/footer Forms views.

Run:

```bash
dotnet build src/Controls/samples/AndroidEmptyViewAdapterHeaderFooterHolderRetentionRepro/AndroidEmptyViewAdapterHeaderFooterHolderRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r artifacts/bin/AndroidEmptyViewAdapterHeaderFooterHolderRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidemptyviewadapterheaderfooterholderretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidemptyviewadapterheaderfooterholderretentionrepro/crc64eaeae278d693e871.MainActivity
adb shell run-as com.microsoft.maui.androidemptyviewadapterheaderfooterholderretentionrepro cat files/autorun-results.txt
```
