# Android SimpleViewHolder Text Retention Repro

This repro exercises the Android no-template string `Header`/`Footer` paths that create direct native `SimpleViewHolder` rows for `CollectionView` chrome.

It retains only the native RecyclerView holder objects after header/footer cleanup and adapter recycle/dispose. The control run explicitly clears each retained native `TextView.Text` slot. The current MAUI run uses the framework cleanup path, where `StructuredItemsViewAdapter` inherits `ItemsViewAdapter.OnViewRecycled()` without `SimpleViewHolder` cleanup and `EmptyViewAdapter` delegates to `SimpleViewHolder.Recycle()`, which only recycles `SizedItemContentView` and leaves direct text holders assigned.

Run:

```bash
dotnet build src/Controls/samples/AndroidSimpleViewHolderTextRetentionRepro/AndroidSimpleViewHolderTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r artifacts/bin/AndroidSimpleViewHolderTextRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidsimpleviewholdertextretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidsimpleviewholdertextretentionrepro/crc64c51e9fbf460878e1.MainActivity
adb shell run-as com.microsoft.maui.androidsimpleviewholdertextretentionrepro cat files/autorun-results.txt
```
