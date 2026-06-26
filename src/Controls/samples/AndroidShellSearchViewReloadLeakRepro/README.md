# Android ShellSearchView Reload Leak Repro

This repro exercises the Android Shell search view path used when a Shell toolbar swaps between pages with different `SearchHandler` instances.

`ShellToolbarTracker.UpdateToolbarItems()` reuses one `ShellSearchView` and calls `LoadView()` again when the current page's `SearchHandler` changes. The current `LoadView()` implementation appends a new native child tree and subscribes to the new `SearchHandler`, but it does not remove/dispose the old native child tree or unsubscribe the old handler first.

The app compares that behavior against a control path that replaces/disposes the search view before moving to the next cached page handler. It reports the retained native child-tree count and total native view count, so repeated page/search-handler swaps show the severity directly.
