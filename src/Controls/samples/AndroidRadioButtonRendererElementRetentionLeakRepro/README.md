# AndroidRadioButtonRendererElementRetentionLeakRepro

This repro exercises Android compatibility `RadioButtonRenderer` disposal.

The control path disposes each native renderer and then clears the protected `Element` property by reflection. The current path keeps disposed native renderers alive without clearing that property. If Android or app code retains disposed native renderer peers, the property keeps disconnected `RadioButton` graphs and their payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidradiobuttonrendererelementretentionleakrepro cat files/autorun-results.txt
```
