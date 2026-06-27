# AndroidRefreshViewRendererElementRetentionLeakRepro

This repro exercises Android compatibility `RefreshViewRenderer` disposal.

The control path disposes each native renderer and then clears the auto-property backing field for `Element` by reflection. The current path keeps disposed native renderers alive without clearing that field. If Android or app code retains disposed native renderer peers, the field keeps disconnected `RefreshView` graphs and their payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidrefreshviewrendererelementretentionleakrepro cat files/autorun-results.txt
```
