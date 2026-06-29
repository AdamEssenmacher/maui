# Android Shell Navigation Icon Retention Repro

This sample proves that Android `ShellToolbarTracker` copies Shell back/flyout icon drawables into retained native toolbar navigation icon state and does not clear that native slot during tracker teardown.

The repro creates transient Shell toolbar trackers with generated 512x512 bitmap `BackButtonBehavior.IconOverride` values, exercises the real `ShellToolbarTracker.UpdateLeftBarButtonItem` path until `Toolbar.NavigationIcon` holds the generated payload, clears the toolbar navigation click listener in both runs to isolate from older tracker-listener retention, and then keeps only native toolbar peers alive. The control run explicitly clears `NavigationIcon` before tracker disposal. The current run uses MAUI's cleanup path as-is.
