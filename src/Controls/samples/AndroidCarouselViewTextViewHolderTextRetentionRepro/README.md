# Android CarouselView TextViewHolder Text Retention Repro

This repro exercises the Android no-template `CarouselViewAdapter` path that creates direct native `TextViewHolder` rows for `CarouselView` items.

It retains only the native RecyclerView holder objects after item source cleanup and adapter recycle/dispose. The control run explicitly clears each retained native `TextView.Text` slot. The current MAUI run uses the framework cleanup path, where `CarouselViewAdapter` inherits `ItemsViewAdapter.OnViewRecycled()`, which only recycles `TemplatedItemViewHolder` and leaves direct `TextViewHolder` native text assigned.

Run:

```bash
dotnet build src/Controls/samples/AndroidCarouselViewTextViewHolderTextRetentionRepro/AndroidCarouselViewTextViewHolderTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r artifacts/bin/AndroidCarouselViewTextViewHolderTextRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidcarouselviewtextviewholdertextretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidcarouselviewtextviewholdertextretentionrepro/crc644da9a19433972afa.MainActivity
adb shell run-as com.microsoft.maui.androidcarouselviewtextviewholdertextretentionrepro cat files/autorun-results.txt
```
