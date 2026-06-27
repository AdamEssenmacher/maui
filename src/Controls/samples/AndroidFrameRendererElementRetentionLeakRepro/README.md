# AndroidFrameRendererElementRetentionLeakRepro

This repro exercises the shipped Android compatibility `FrameRenderer`.

The control path disposes each native renderer and clears the private `_element` field plus the `MotionEventHelper2` element by reflection. The current path keeps disposed native renderers alive without clearing those fields. If Android or app code retains disposed native renderer peers, the fields keep disconnected `Frame` graphs and their payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidframerendererelementretentionleakrepro cat files/autorun-results.txt
```
