# Android Legacy TabbedPageRenderer Bottom Tab Icon Retention Repro

This sample proves that obsolete Android compatibility `TabbedPageRenderer` copies child page icons into retained bottom-tab native menu item peers and does not clear those native icon slots during renderer disposal.

The repro creates transient bottom-placement legacy `TabbedPageRenderer` instances with generated 512x512 bitmap tab icons, drives the renderer's real `SetElement(...)` / `SetupBottomNavigationView(...)` path so MAUI assigns those icons through `BottomNavigationView` menu item icons, disposes the renderers, clears MAUI-side page icon state, and then keeps only JNI global references to the native `BottomNavigationView` peers. The control run explicitly clears native menu item icon slots before renderer disposal. The current run uses MAUI's cleanup path as-is.
