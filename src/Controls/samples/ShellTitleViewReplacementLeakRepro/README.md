# Shell TitleView Replacement Leak Repro

This repro exercises the iOS/Mac Catalyst Shell title-view replacement path.

`ShellPageRendererTracker.UpdateTitleView()` disconnects the old native title-view container only when the next title view is `null`. When Shell swaps from one non-null title view to another, it assigns a new `TitleViewContainer` to `NavigationItem.TitleView` without disconnecting the old retained title view's handler.

The repro models cached Shell pages by retaining old title-view virtual views while the toolbar replaces the active native title-view container. It compares the current replacement behavior against a control path that explicitly disconnects each old title-view handler before replacement.
