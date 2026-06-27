# AndroidIndicatorViewTemplateDisconnectLeakRepro

This repro exercises Android `IndicatorView` template cleanup during handler disconnect.

`MauiPageControl.SetIndicatorView(null)` calls `RemoveViews(0)` during `IndicatorViewHandler` disconnect. When the indicator uses a template, `_isTemplateIndicator` makes `RemoveViews` return immediately, so the template layout native child remains attached to the retained native `MauiPageControl`. That child can keep the template layout handler, the `IndicatorStackLayout`, the old `IndicatorView`, and item payloads alive after disconnect.

The control path explicitly disconnects the template layout handler and removes native children before disconnecting the `IndicatorViewHandler`. The current path uses normal MAUI disconnect.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidindicatorviewtemplatedisconnectleakrepro cat files/autorun-results.txt
```
