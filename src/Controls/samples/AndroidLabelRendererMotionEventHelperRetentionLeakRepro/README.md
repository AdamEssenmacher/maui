# Android LabelRenderer MotionEventHelper Retention Leak Repro

This sample proves that disposing legacy Android `LabelRenderer` does not clear the renderer's `MotionEventHelper`.

`LabelRenderer.OnElementChanged()` calls `MotionEventHelper.UpdateElement(e.NewElement)`. `LabelRenderer` inherits the base dispose path; base `VisualElementRenderer<Label>` clears its own `Element`, but the helper still holds the old `Label`.

The autorun compares current disposal with a control path that clears the helper's private `_element` field after disposal.
