# Android AlertManager HandlerChanged Retention Repro

This sample proves that Android `AlertManager.AlertRequestHelper.WaitForHandlerIfNeeded()` can retain pending dialog argument graphs when `DisplayAlert`, `DisplayActionSheet`, or `DisplayPrompt` is requested before the page has a handler.

The autorun creates handlerless pages and queues one alert, action sheet, and prompt request per page. Both runs retain the handlerless pages to model real apps that build or cache pages before attachment. The control run clears `Element.HandlerChanged` after requests are queued. Current MAUI leaves three pending `HandlerChanged` callbacks per page, and those callbacks retain the dialog arguments and generated payload strings until a handler is assigned.

Build:

```bash
dotnet build src/Controls/samples/AndroidAlertManagerHandlerChangedRetentionRepro/AndroidAlertManagerHandlerChangedRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

Run:

```bash
adb install --no-incremental -r artifacts/bin/AndroidAlertManagerHandlerChangedRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidalertmanagerhandlerchangedretentionrepro-Signed.apk
adb shell am start -S -n com.microsoft.maui.androidalertmanagerhandlerchangedretentionrepro/crc64e124a72a7b2e8db5.MainActivity
adb shell run-as com.microsoft.maui.androidalertmanagerhandlerchangedretentionrepro cat files/autorun-results.txt
```
