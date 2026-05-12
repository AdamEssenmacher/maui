# Android WebView file chooser callback leak repro

This standalone app demonstrates the Android leak where completed WebView file chooser callbacks remain rooted by `Microsoft.Maui.Platform.ActivityResultCallbackRegistry`.

`MauiWebChromeClient` registers a static activity result callback for each file chooser request. On current unfixed builds, `ActivityResultCallbackRegistry.InvokeCallback` invokes the callback but leaves it in the static dictionary, so the callback closure and `IValueCallback` stay alive after the Android picker completes.

## Build

From the repository root:

```bash
dotnet build src/Controls/samples/AndroidWebViewFileChooserCallbackLeakRepro/Maui.Controls.AndroidWebViewFileChooserCallbackLeakRepro.csproj -f net10.0-android
```

## Install and run

```bash
dotnet build src/Controls/samples/AndroidWebViewFileChooserCallbackLeakRepro/Maui.Controls.AndroidWebViewFileChooserCallbackLeakRepro.csproj -f net10.0-android -t:Install -p:AndroidDeviceSerial=emulator-5554
adb -s emulator-5554 shell monkey -p com.microsoft.maui.repros.androidwebviewfilechoosercallbackleak 1
```

## Manual repro steps

1. Tap the WebView file input and cancel the Android picker.
2. Tap `Force GC`.
3. Tap `Open tracked chooser` and cancel the Android picker.
4. Tap `Force GC`.

On an unfixed build, the registry callback count increases after each completed chooser. After the tracked chooser completes, the tracked callback remains alive after GC because the static registry still roots the callback closure.

With a fix that removes callbacks from `ActivityResultCallbackRegistry` after `OnActivityResult`, the registry callback count returns to baseline and completed tracked callbacks are finalized after GC.
