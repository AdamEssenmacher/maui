# IosTabletFlyoutPageBackgroundPatternRetentionRepro

This Mac Catalyst repro checks whether legacy iOS `TabletFlyoutPageRenderer` leaves generated
background pattern images alive on retained native root `UIView` peers after renderer disposal.

The control scenario clears the retained native view background color after disposal. The current
MAUI scenario leaves the pattern background assigned and verifies whether the FlyoutPage, payload,
and image source collect.
