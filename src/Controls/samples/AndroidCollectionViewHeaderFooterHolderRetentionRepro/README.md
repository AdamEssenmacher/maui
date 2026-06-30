# Android CollectionView Header/Footer Holder Retention Repro

This repro exercises the Android `StructuredItemsViewAdapter` path for plain Forms-view `CollectionView.Header` and `CollectionView.Footer` values.

It retains only the native RecyclerView holder objects after header/footer removal and adapter recycle/dispose. The control run explicitly removes the logical child, recycles the `ItemContentView`, and clears holder references. The current MAUI run uses the framework cleanup path, where `ItemsViewAdapter.OnViewRecycled()` ignores `SimpleViewHolder`.

Run:

```bash
dotnet build src/Controls/samples/AndroidCollectionViewHeaderFooterHolderRetentionRepro/AndroidCollectionViewHeaderFooterHolderRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r artifacts/bin/AndroidCollectionViewHeaderFooterHolderRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidcollectionviewheaderfooterholderretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidcollectionviewheaderfooterholderretentionrepro/crc643ef254eae46e2079.MainActivity
adb shell run-as com.microsoft.maui.androidcollectionviewheaderfooterholderretentionrepro cat files/autorun-results.txt
```
