# Android Legacy TabbedPageRenderer Tab Icon Retention Repro

This sample proves that obsolete Android compatibility `TabbedPageRenderer` copies child page icons into retained top-tab native peers and does not clear those native icon slots during renderer disposal.

The repro creates transient legacy `TabbedPageRenderer` instances with generated 512x512 bitmap tab icons, drives the renderer's real `SetTabIconImageSource(Page, TabLayout.Tab)` path so MAUI assigns those icons through `TabLayout.Tab.SetIcon`, disposes the renderers, clears MAUI-side page icon state, and then keeps only JNI global references to the native `TabLayout` peers. The control run explicitly clears native tab icon slots before renderer disposal. The current run uses MAUI's cleanup path as-is.
