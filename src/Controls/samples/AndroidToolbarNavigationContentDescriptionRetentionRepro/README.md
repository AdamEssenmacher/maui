# Android Toolbar Navigation ContentDescription Retention Repro

This repro isolates the Android toolbar navigation content-description slot.

`Toolbar.MapBackButtonTitle()` calls `UpdateBackButton()`, which copies
`Toolbar.BackButtonTitle` into the native `MaterialToolbar.NavigationContentDescription`
slot. Shell toolbar code uses the same native slot for `BackButtonBehavior`
text and flyout/back image automation IDs. Toolbar disconnect removes the native
toolbar from its parent but does not clear the retained navigation content
description.

The sample uses ordinary current `ToolbarHandler` instances, retains only the
native `MaterialToolbar` peers, compares current MAUI cleanup with an explicit
native content-description clear, writes `autorun-results.txt` under app data,
and exits.
