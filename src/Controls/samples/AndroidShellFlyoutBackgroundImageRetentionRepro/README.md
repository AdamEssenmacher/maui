# Android Shell Flyout Background Image Retention Repro

This repro exercises the Android compatibility `ShellFlyoutTemplatedContentRenderer` flyout background image path.

It assigns a realistic generated `Shell.FlyoutBackgroundImage`, waits for the real renderer to copy it into the native `_bgImage` `ImageView`, detaches and retains only that native image-view peer, and compares current renderer disposal with an explicit native `SetImageDrawable(null)` cleanup.
