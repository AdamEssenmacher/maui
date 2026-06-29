# Android Picker Dialog String Retention Repro

This repro exercises the current Android `PickerHandler` path that creates an `AlertDialog` through `MaterialAlertDialogBuilder`, assigns `IPicker.Title` to the native dialog title, and assigns every picker item string through `SetSingleChoiceItems`.

The harness retains dismissed native dialogs to model delayed Android/native dialog lifetime. In both the control and current runs it clears the native list item-click callback so the test does not depend on the dialog callback retaining the handler. The control also clears native dialog title/list text state before handler disconnect. Current MAUI dismisses and drops the handler field reference but does not clear those native strings.

Run:

```sh
dotnet build src/Controls/samples/AndroidPickerDialogStringRetentionRepro/AndroidPickerDialogStringRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
adb install -r --no-incremental artifacts/bin/AndroidPickerDialogStringRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidpickerdialogstringretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidpickerdialogstringretentionrepro/crc64d41bceb7b035ab07.MainActivity
adb shell run-as com.microsoft.maui.androidpickerdialogstringretentionrepro cat files/autorun-results.txt
```
