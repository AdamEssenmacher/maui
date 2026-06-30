# Android ItemsView TextViewHolder Text Retention Repro

This repro exercises the Android no-template `ItemsViewAdapter` path that creates direct native `TextViewHolder` rows for `CollectionView` items.

It retains only the native RecyclerView holder objects after item source cleanup and adapter recycle/dispose. The control run explicitly clears each retained native `TextView.Text` slot. The current MAUI run uses the framework cleanup path, where `ItemsViewAdapter.OnViewRecycled()` only recycles `TemplatedItemViewHolder` and leaves direct `TextViewHolder` native text assigned.

Run:

```bash
dotnet build src/Controls/samples/AndroidItemsViewTextViewHolderTextRetentionRepro/AndroidItemsViewTextViewHolderTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r artifacts/bin/AndroidItemsViewTextViewHolderTextRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androiditemsviewtextviewholdertextretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androiditemsviewtextviewholdertextretentionrepro/crc64283e2cf6f9146264.MainActivity
adb shell run-as com.microsoft.maui.androiditemsviewtextviewholdertextretentionrepro cat files/autorun-results.txt
```
