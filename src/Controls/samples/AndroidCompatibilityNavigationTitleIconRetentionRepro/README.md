# Android compatibility NavigationPage title-icon retention repro

This sample exercises the obsolete Android compatibility `NavigationPageRenderer.UpdateTitleIcon()` path. The renderer loads `NavigationPage.TitleIconImageSource` into a private native `_titleIconView` with `ImageView.SetImageDrawable(...)`.

The app creates transient compatibility `NavigationPageRenderer` instances with generated 512x512 title-icon images and retains JNI global references to the native title-icon `ImageView` peers after renderer disposal. It compares current cleanup with a control run that explicitly clears the native title-icon drawable before disposal.

Expected proof shape:

- Control run: retained native title-icon peers stay alive, but their drawable slots are empty.
- Current run: retained native title-icon peers keep every generated drawable while the compatibility renderers, NavigationPages, pages, and image sources collect.
