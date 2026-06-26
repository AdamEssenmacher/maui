# ShellFlyoutHeaderDisconnectLeakRepro

This Mac Catalyst repro exercises the Shell flyout header replacement teardown path.

`ShellFlyoutContentRenderer.UpdateFlyoutHeader()` removes and disposes the old `ShellFlyoutHeaderContainer` when `Shell.FlyoutHeader` changes. That container inherits `UIContainerView`, whose `Disconnect()` method is empty; disposing the container removes the native child but does not disconnect the MAUI handler for the old header view. The footer path in the same renderer explicitly clears the old view handler and calls `DisconnectHandler()`.

The repro retains removed header views in both scenarios, matching a realistic app cache/reuse pattern:

- Control: create a flyout header container, explicitly disconnect the removed header view handler, then dispose the container.
- Leak: create the same container and dispose it without disconnecting the removed header view handler, matching the current header replacement path.

Each removed flyout header handler carries a 1 MiB payload. A proved run retains all removed header views in both scenarios, but only the current header-disposal scenario retains all handlers and payloads.

Run:

```sh
dotnet build src/Controls/samples/ShellFlyoutHeaderDisconnectLeakRepro/ShellFlyoutHeaderDisconnectLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W src/Controls/samples/ShellFlyoutHeaderDisconnectLeakRepro/bin/Debug/net10.0-maccatalyst/maccatalyst-*/ShellFlyoutHeaderDisconnectLeakRepro.app --args --auto-run --results=/tmp/shellflyoutheaderdisconnectleakrepro-results.txt
cat /tmp/shellflyoutheaderdisconnectleakrepro-results.txt
```
