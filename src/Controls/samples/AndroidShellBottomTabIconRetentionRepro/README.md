# Android Shell Bottom Tab Icon Retention Repro

This sample proves that Android `ShellItemRenderer` copies Shell section icons into retained native bottom-tab peers and does not clear those native icon slots during renderer teardown.

The repro creates transient Shell tab bars with generated 512x512 bitmap section icons, exercises the real `ShellItemRenderer.SetupMenu` path until `BottomNavigationViewUtils.SetMenuItemIcon` assigns those icons through `IMenuItem.SetIcon`, clears native title slots in both runs, and then keeps only the native `BottomNavigationView` peers alive by JNI global reference. The control run explicitly clears native menu icon slots before renderer destruction. The current run uses MAUI's cleanup path as-is.
