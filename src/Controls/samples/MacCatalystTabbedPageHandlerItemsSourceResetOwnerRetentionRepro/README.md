# Mac Catalyst TabbedPage handler ItemsSource reset owner retention repro

This Mac Catalyst repro checks whether app-retained `TabbedPage` child pages generated from `ItemsSource` keep discarded handler-based `TabbedPage` owners alive after an `ItemsSource.Clear()` reset leaves stale child `Page.PropertyChanged` subscriptions attached by `TabbedPage.OnHandlerChangingCore()`.

It compares current MAUI with a control that removes only the handler-local generated child page subscriptions before the `ItemsSource` reset. Both runs retain the same generated child pages, detach the owner handler, and call `ClearLogicalChildren()` after reset so the measured graph is not the known `MultiPage<T>.ItemsSource` stale logical-child retention.

The autorun uses 96 `TabbedPage` owners with 3 generated pages each and a 1 MiB synthetic owner payload per tabbed workflow. The expected current MAUI result is `RESULT: PROVEN` with 288 retained generated child subscriptions, 96 retained discarded `TabbedPage` owners, and 96.0 MiB of retained owner payload. Results are written to `/tmp/maccatalyst-tabbedpage-handler-itemssource-reset-owner-retention-results.txt`.
