# Shell MenuItemTracker Target Leak Repro

This repro proves that clearing a `MenuBarTracker` target does not fully detach when the target is a live `Shell`.

`MenuItemTracker<T>.TrackTarget(Page)` subscribes to `GetMenuItems(page).CollectionChanged` before the Shell-specific branch. The Shell-specific `UntrackTarget(Page)` branch removes Shell navigation events but returns before removing the menu collection subscription or the current Shell page subscriptions. A live Shell can therefore retain a cleared tracker, and `MenuBarTracker` retains its owner element through `_parent`.

Run on Mac Catalyst:

```bash
dotnet build src/Controls/samples/ShellMenuItemTrackerTargetLeakRepro/ShellMenuItemTrackerTargetLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ShellMenuItemTrackerTargetLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellMenuItemTrackerTargetLeakRepro.app --args --auto-run --results=/tmp/shellmenuitemtrackertargetleakrepro-results.txt
cat /tmp/shellmenuitemtrackertargetleakrepro-results.txt
```
