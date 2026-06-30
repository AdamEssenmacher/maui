# Android Date/Time Picker Dialog Callback Retention Repro

This repro compares current Android `DatePickerHandler` / `TimePickerHandler` dialog callbacks with control handlers that create equivalent native dialogs using weak callbacks.

The app retains native `DatePickerDialog` and `TimePickerDialog` peers after handler disconnect. Current handlers create constructor callbacks that capture the handler instance; the control path keeps the same native-dialog lifetime but avoids a strong callback-to-handler edge. Each cycle uses a fresh `MauiContext` backed by a 512 KiB payload service provider so retained disconnected-handler context graphs show up as payload retention.

Run:

```sh
dotnet build src/Controls/samples/AndroidDateTimePickerDialogCallbackRetentionRepro/AndroidDateTimePickerDialogCallbackRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r --no-incremental artifacts/bin/AndroidDateTimePickerDialogCallbackRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androiddatetimepickerdialogcallbackretentionrepro-Signed.apk
adb shell monkey -p com.microsoft.maui.androiddatetimepickerdialogcallbackretentionrepro 1
adb shell run-as com.microsoft.maui.androiddatetimepickerdialogcallbackretentionrepro cat files/autorun-results.txt
```
