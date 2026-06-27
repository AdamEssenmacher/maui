# Android ImageRenderer MotionEventHelper Retention Leak Repro

This sample proves that disposing legacy Android `ImageRenderer` does not clear the renderer's `MotionEventHelper`.

`ImageRenderer.OnElementChanged()` calls `MotionEventHelper.UpdateElement(e.NewElement)`. `ImageRenderer.Dispose(bool)` only gates repeated disposal and then calls base cleanup. Base `VisualElementRenderer<Image>` clears its own `Element`, but the helper still holds the old `Image`.

The autorun compares current disposal with a control path that clears the helper's private `_element` field after disposal.
