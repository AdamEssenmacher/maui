# Android BoxRenderer MotionEventHelper Retention Leak Repro

This sample proves that disposing legacy Android `BoxRenderer` does not clear the renderer's `MotionEventHelper`.

`BoxRenderer.OnElementChanged()` calls `MotionEventHelper.UpdateElement(e.NewElement)`. `BoxRenderer.Dispose(bool)` clears renderer subscriptions and then relies on the base `VisualElementRenderer<BoxView>` cleanup to clear `Element`, but it never calls `UpdateElement(null)` on the helper. If a disposed native renderer peer remains rooted, the helper keeps the disconnected `BoxView` and binding payload alive.

The autorun compares current disposal with a control path that clears the helper's private `_element` field after disposal.
