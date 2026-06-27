# AndroidNativeCellViewCellRetentionLeakRepro

This repro exercises Android compatibility native cell-view cleanup.

The control path clears the native `BaseCellView._cell` back-reference before disconnecting the `TextCellRenderer`. The current path calls normal `CellRenderer.DisconnectHandler()`, which removes renderer-side subscriptions but does not clear the native row view's strong `Cell` reference. If Android keeps native row views rooted, those row views keep old cells and cell binding payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidnativecellviewcellretentionleakrepro cat files/autorun-results.txt
```
