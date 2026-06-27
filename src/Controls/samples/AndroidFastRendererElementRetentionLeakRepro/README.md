# AndroidFastRendererElementRetentionLeakRepro

This repro exercises Android compatibility FastRenderer disposal.

The control path disposes each native renderer and then clears the stale private virtual-element fields (`_element`, `_button`, and `MotionEventHelper._element`) by reflection. The current path keeps disposed native renderers alive without clearing those fields. If Android or app code retains disposed native renderer peers, the fields keep disconnected `Label`, `Button`, `Image`, and `Frame` graphs and their payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidfastrendererelementretentionleakrepro cat files/autorun-results.txt
```
