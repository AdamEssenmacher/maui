# ShellFlyoutUIContainerCellLeakRepro

This Mac Catalyst repro exercises the Shell flyout `UIContainerCell` teardown path.

`ShellTableViewSource` caches `UIContainerCell` instances for flyout item template views. `UIContainerCell` adds each view as a logical child of the Shell item and subscribes to `View.MeasureInvalidated`. Its `Disconnect()` method removes the logical child, clears the source-style invalidation callback, detaches the measure event, and clears the handler. `ShellTableViewController.Dispose()` detaches its own events but does not disconnect the cached cells.

The repro retains realistic live `Shell` roots in both scenarios:

- Control: create a flyout cell and call `UIContainerCell.Disconnect()` before disposing the cell.
- Leak: create the same cell and dispose it without calling `Disconnect()`, matching the current source-disposal gap.

Each flyout template view carries a 1 MiB payload. A proved run retains all disposed cells, flyout views, handlers, source-style cache owners, and payloads only in the missing-disconnect scenario.

Run:

```sh
dotnet build src/Controls/samples/ShellFlyoutUIContainerCellLeakRepro/ShellFlyoutUIContainerCellLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W src/Controls/samples/ShellFlyoutUIContainerCellLeakRepro/bin/Debug/net10.0-maccatalyst/maccatalyst-*/ShellFlyoutUIContainerCellLeakRepro.app --args --auto-run --results=/tmp/shellflyoutuicontainercellleakrepro-results.txt
cat /tmp/shellflyoutuicontainercellleakrepro-results.txt
```
