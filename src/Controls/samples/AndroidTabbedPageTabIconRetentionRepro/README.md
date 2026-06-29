# Android TabbedPage Tab Icon Retention Repro

This sample proves that Android `TabbedPageManager` copies child page icons into retained native tab peers and does not clear those native icon slots during `SetElement(null)`.

The repro creates transient top-tab and bottom-tab `TabbedPage` managers with generated 512x512 bitmap tab icons, waits for MAUI to assign those icons through `TabLayout.Tab.SetIcon` and `IMenuItem.SetIcon`, disconnects the managers, clears MAUI-side page icon state, and then keeps only the native `TabLayout` / `BottomNavigationView` peers alive. The control run explicitly clears native tab/menu icon slots after disconnect. The current run uses MAUI's cleanup path as-is.
