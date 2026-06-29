# Android TabbedPage Tab Title Retention Repro

This sample proves that Android `TabbedPageManager` copies child page titles into retained native tab peers and does not clear those native title slots during `SetElement(null)`.

The repro creates transient top-tab and bottom-tab `TabbedPage` managers with generated 8 KiB tab titles, disconnects them, clears MAUI-side page title state, and then keeps only the native `TabLayout` / `BottomNavigationView` peers alive. The control run explicitly clears native tab/menu titles after disconnect. The current run uses MAUI's cleanup path as-is.
