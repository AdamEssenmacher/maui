# AndroidHandlerToRendererShimRendererPropertyRetentionLeakRepro

This repro exercises Android compatibility `HandlerToRendererShim` disposal.

The current path models a removed compatibility child view that app code still references. `HandlerToRendererShim.Dispose()` disconnects the handler but does not clear the element's `Platform.RendererProperty`, unsubscribe `Element.PropertyChanged`, clear its own `Element`, or dispose/clear its tracker, so the retained element keeps the disposed shim and handler alive. The payload is stored only on the handler to prove the extra retention caused by the stale bridge state.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidhandlertorenderershimrendererpropertyretentionleakrepro cat files/autorun-results.txt
```
