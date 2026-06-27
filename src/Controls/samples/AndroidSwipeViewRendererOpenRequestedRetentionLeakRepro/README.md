# AndroidSwipeViewRendererOpenRequestedRetentionLeakRepro

This repro exercises Android compatibility `SwipeViewRenderer` element replacement.

`SwipeViewRenderer.OnElementChanged()` subscribes `OpenRequested` and `CloseRequested` on the new element. When replacing an old element, it removes `CloseRequested` from the old element but removes `OpenRequested` from `e.NewElement` instead of `e.OldElement`. A retained old `SwipeView` can therefore keep the reused renderer alive, and the renderer keeps the newer payload-bearing `SwipeView` alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidswipeviewrendereropenrequestedretentionleakrepro cat files/autorun-results.txt
```
