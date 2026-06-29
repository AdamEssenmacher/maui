# Android toolbar title-icon drawable retention repro

This sample exercises the Android `Toolbar.TitleIcon` path through the real `ToolbarHandler` and `Toolbar.MapTitleIcon(...)` mapper.

The app creates transient toolbars with generated 512x512 ARGB title icons and retains the native toolbar/title-icon peers after disconnect. It compares current cleanup with a control run that explicitly clears the nested title-icon `ImageView` drawable before disconnect.

Expected proof shape:

- Control run: retained native title-icon peers stay alive, but their drawable slots are empty.
- Current run: retained native title-icon peers keep every generated drawable while the virtual toolbars, handlers, toolbar title-icon sources, and payload image sources collect.
