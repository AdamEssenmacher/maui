# NavigationRenderer pending task retention repro

This Mac Catalyst repro targets `Microsoft.Maui.Controls.Handlers.Compatibility.NavigationRenderer`.

The production renderer creates `_pendingNavigationRequest` in `GetAppearedOrDisappearedTask()` and completes it from lifecycle/delegate callbacks. `Dispose()` tears down the renderer, navigation subscriptions, child view controllers, and gesture delegate, but does not call `CompletePendingNavigation(false)`. A live `NavigationPage` can therefore keep its current navigation task incomplete after handler disposal.

The sample runs two scenarios with 80 pending navigation operations and 1 MiB payloads:

- control: complete the renderer pending navigation before disposing the renderer
- current: dispose the renderer while the pending navigation is still assigned

The result file is written to `Path.GetTempPath()/navigationrenderer-pending-task-retention-results.txt`, and the app prints the exact path in the report.
