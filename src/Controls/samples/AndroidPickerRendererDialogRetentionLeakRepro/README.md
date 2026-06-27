# Android PickerRenderer Dialog Retention Leak Repro

This sample proves that the legacy Android compatibility `PickerRendererBase<TControl>` can retain disposed picker graphs when the renderer is disposed while its native `AlertDialog` is open.

Run:

```sh
dotnet build src/Controls/samples/AndroidPickerRendererDialogRetentionLeakRepro/AndroidPickerRendererDialogRetentionLeakRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
adb install -r --no-incremental artifacts/bin/AndroidPickerRendererDialogRetentionLeakRepro/Debug/net10.0-android/com.microsoft.maui.androidpickerrendererdialogretentionleakrepro-Signed.apk
adb shell monkey -p com.microsoft.maui.androidpickerrendererdialogretentionleakrepro 1
adb shell run-as com.microsoft.maui.androidpickerrendererdialogretentionleakrepro cat files/autorun-results.txt
```
