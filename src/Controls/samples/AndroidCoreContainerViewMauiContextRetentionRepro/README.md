# AndroidCoreContainerViewMauiContextRetentionRepro

This repro isolates Android core `Microsoft.Maui.Platform.ContainerView._context` retention.

The app runs automatically on startup and writes `autorun-results.txt` into app data. It creates 96 core `ContainerView` instances per scenario, assigns a hosted `BoxView`, clears `CurrentView`, disconnects the hosted handler, and retains the same native container peer shape after teardown. Each synthetic `MauiContext` resolves a unique 1 MiB payload service through its service provider.

The control run reflection-clears private `ContainerView._context` after teardown. The current-MAUI run leaves `_context` assigned. A proved run shows the hosted views and handlers collect in both scenarios, the control context/service graph collects, and the current-MAUI context/service graph remains alive through retained container peers.
