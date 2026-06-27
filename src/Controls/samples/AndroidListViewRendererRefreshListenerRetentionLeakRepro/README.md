# AndroidListViewRendererRefreshListenerRetentionLeakRepro

This repro exercises Android compatibility `ListViewRenderer` disconnect cleanup.

The control path explicitly runs the renderer's old-element cleanup before handler disconnect. The current path calls normal `IElementHandler.DisconnectHandler()`, which clears the renderer's virtual view but does not run `ListViewRenderer.OnElementChanged(old, null)`. If the native `SwipeRefreshLayout` container remains rooted, the renderer keeps its `ListViewAdapter`, and the adapter strongly retains the old `ListView` plus its item payloads.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidlistviewrendererrefreshlistenerretentionleakrepro cat files/autorun-results.txt
```
