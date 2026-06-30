# AndroidFrameRendererMauiContextRetentionRepro

This repro exercises the shipped Android compatibility `FrameRenderer`.

It retains disposed native renderer peers in both runs. Both runs clear the known C122 `_element` and `MotionEventHelper2._element` roots so the `Frame` can collect. The control also clears the private `_mauiContext` field. Current MAUI leaves `_mauiContext` assigned, so retained renderer peers keep old window-scoped service providers and services alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidframerenderermauicontextretentionrepro cat files/autorun-results.txt
```
