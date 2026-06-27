# Android DefaultRenderer MotionEventHelper Retention Leak Repro

This sample proves that disposing legacy Android AppCompat `Platform.DefaultRenderer` does not clear the renderer's `MotionEventHelper`.

`DefaultRenderer.OnElementChanged()` calls `MotionEventHelper.UpdateElement(e.NewElement)`. `DefaultRenderer.Dispose(bool)` clears its touch listener and then calls base cleanup. Base `VisualElementRenderer<View>` clears its own `Element`, but the helper still holds the old `View`.

The autorun compares current disposal with a control path that clears the helper's private `_element` field after disposal.
