# AndroidToolbarTitleViewRetentionLeakRepro

This repro exercises `Toolbar.Android.UpdateTitleView()` with live Android toolbar handlers.

The control path clears the retained toolbar title-view handler/container state and then disconnects the removed title-view handler after `Toolbar.TitleView = null`.
The current MAUI path only clears `Toolbar.TitleView`, which removes the native container from the toolbar but leaves the old title-view handler reachable through toolbar fields.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidtoolbartitleviewretentionleakrepro cat files/autorun-results.txt
```
