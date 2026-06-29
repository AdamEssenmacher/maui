# Android Toolbar Menu Item Icon Retention Repro

This sample exercises the real Android `ToolbarHandler` / `Toolbar.MapToolbarItems()` path.
It keeps native `MaterialToolbar` peers alive after handler disconnect and compares current
MAUI behavior with a control path that explicitly clears each retained native `IMenuItem.Icon`.

The toolbar item title and automation id are intentionally short, and native click listeners are
cleared in both runs, so the result is isolated from existing toolbar listener and native string
retention repros.
