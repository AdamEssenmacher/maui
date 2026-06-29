# Android accessible tap delegate retention repro

This repro exercises the real Android Controls accessible-tap delegate path.

`View.HandlerChangedPartial()` calls `AddOrRemoveControlsAccessibilityDelegate()` for a `BoxView` with a single-primary-button `TapGestureRecognizer`. That installs a `ControlsAccessibilityDelegate` on the retained native Android view. The delegate strongly stores the `IViewHandler`.

`ViewHandler.Android.DisconnectingHandler()` clears only top-level `MauiAccessibilityDelegateCompat` instances. It does not unwrap or clear `ControlsAccessibilityDelegate`, so a retained native view can keep a disconnected handler alive. `ElementHandler.DisconnectHandler()` clears `VirtualView` and `PlatformView`, but it leaves `MauiContext` assigned; this repro gives each disconnected handler a 1 MiB service-provider payload to show the severity of that stale context root.

The control run explicitly clears the native accessibility delegate after handler disconnect. The current-MAUI run leaves the delegate assigned.
