# Android More bottom sheet row-state retention repro

This sample exercises the shared Android `BottomNavigationViewUtils.CreateMoreBottomSheet(...)` implementation used by Shell and `TabbedPage` overflow navigation.

The app creates transient More bottom sheet dialogs with realistic generated row titles and 256x256 ARGB icon payloads. It keeps only the native row `ImageView` and `TextView` peers alive, then compares current cleanup with a control run that explicitly clears each native row drawable and title before teardown.

Expected proof shape:

- Control run: retained native row peers stay alive, but their drawable/title slots are empty.
- Current run: retained native row peers keep all generated icon drawables and title text after dialog disposal while the transient MAUI context, service provider, image sources, and managed title strings collect.
