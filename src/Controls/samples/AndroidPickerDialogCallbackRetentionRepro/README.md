# Android Picker Dialog Callback Retention Repro

This repro compares current Android `PickerHandler` / Material3 `PickerHandler2` dialog item callbacks with equivalent native picker dialogs whose item callbacks keep only a weak reference to the handler.

The app retains dismissed native picker `AlertDialog` peers after handler disconnect. Current MAUI dialog item callbacks are lambdas that capture the handler instance; the control path keeps the same retained native-dialog lifetime but avoids a strong callback-to-handler edge. Each cycle uses a fresh `MauiContext` backed by a 512 KiB payload service provider so retained disconnected-handler context graphs show up as payload retention.

Run:

```sh
dotnet build src/Controls/samples/AndroidPickerDialogCallbackRetentionRepro/AndroidPickerDialogCallbackRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -t:SignAndroidPackage -v:minimal -clp:Summary
adb install -r --no-incremental artifacts/bin/AndroidPickerDialogCallbackRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidpickerdialogcallbackretentionrepro-Signed.apk
adb shell monkey -p com.microsoft.maui.androidpickerdialogcallbackretentionrepro 1
adb shell run-as com.microsoft.maui.androidpickerdialogcallbackretentionrepro cat files/autorun-results.txt
```
