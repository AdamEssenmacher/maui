# Android toolbar item semantic delegate retention repro

This repro exercises the real Android `ToolbarHandler` and `Toolbar.MapToolbarItems()` path for primary toolbar items.

`ToolbarExtensions.UpdateMenuItem()` calls `SetSemanticProperties()`, which copies `SemanticProperties.Description` and `SemanticProperties.Hint` into a private `AccessibilityDelegateCompatImpl` attached to the native toolbar item view. Toolbar disconnect unsubscribes managed `ToolbarItem.PropertyChanged`, but it does not clear the retained native view's accessibility delegate.

The repro clears native menu click listeners in both runs to isolate from toolbar click-listener retention, clears the managed semantic properties after mapping, and compares current cleanup with explicit native accessibility delegate clearing.
