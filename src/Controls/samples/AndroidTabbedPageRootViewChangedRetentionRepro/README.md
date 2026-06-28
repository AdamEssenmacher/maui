# Android TabbedPage RootViewChanged Retention Repro

This sample proves that `TabbedPageManager.SetTabLayout()` can retain transient Android `TabbedPage` graphs when the navigation root view is not ready.

The current path subscribes `TabbedPageManager.RootViewChanged` to `NavigationRootManager.RootViewChanged`. If the `TabbedPage` disconnects before the root view is created, `TabbedPageManager.SetElement(null)` clears `Element` but does not unsubscribe `RootViewChanged` or clear `previousPage`. A retained `NavigationRootManager` can therefore keep the `TabbedPageManager`, previous/current page, and realistic page payload alive.

The app runs automatically on launch and writes results to `files/autorun-results.txt`.
