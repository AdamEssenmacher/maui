# Android Toolbar Menu Item String Retention Repro

This repro isolates Android toolbar menu-item native string slots.

`Toolbar.MapToolbarItems()` copies `ToolbarItem.Text` into the native Android
`IMenuItem` title and copies `ToolbarItem.AutomationId` into the native menu
item content description. Toolbar disconnect unsubscribes managed property
change handlers, but it does not clear retained native menu-item title or
content-description values.

The sample clears native menu click listeners in both runs so it does not
measure the already-tracked toolbar command/listener leak. It retains only the
native `MaterialToolbar` peers, compares current MAUI cleanup with an explicit
native string-slot clear, writes `autorun-results.txt` under app data, and exits.
