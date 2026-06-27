# AndroidIndicatorViewRendererElementRetentionLeakRepro

This repro exercises Android compatibility `IndicatorViewRenderer` disposal.

The control path disposes each native renderer and then clears the protected `IndicatorView` field by reflection. The current path keeps disposed native renderers alive without clearing that field. If Android or app code retains disposed native renderer peers, the field keeps disconnected `IndicatorView` graphs and their payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidindicatorviewrendererelementretentionleakrepro cat files/autorun-results.txt
```
