# AndroidTableViewRendererAdapterRetentionLeakRepro

This repro exercises Android compatibility `TableViewRenderer` disconnect cleanup.

The control path clears the native `ListView.Adapter` and disposes the renderer's private `TableViewModelRenderer` adapter before disconnect. The current path calls normal `IElementHandler.DisconnectHandler()`, which clears the renderer's virtual view but does not dispose `_adapter`. If the native `ListView` remains rooted, its adapter keeps the old `TableView`, `TableRoot`, cells, and cell binding payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidtableviewrendereradapterretentionleakrepro cat files/autorun-results.txt
```
