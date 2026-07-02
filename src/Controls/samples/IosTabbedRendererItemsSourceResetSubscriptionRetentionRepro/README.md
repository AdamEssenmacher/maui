# iOS TabbedRenderer ItemsSource reset subscription retention repro

This Mac Catalyst repro checks whether app-retained `TabbedPage` child pages generated from `ItemsSource` keep disposed current Controls compatibility `TabbedRenderer` peers alive after an `ItemsSource.Clear()` reset leaves stale child `Page.PropertyChanged` subscriptions attached.

It compares current MAUI with a control that removes only the renderer's generated child page subscriptions before the `ItemsSource` reset. Both runs retain the same generated child pages and disconnect their handlers so the measured retained graph is the disposed renderer and old `MauiContext` service graph, not live child page handlers.

The autorun uses 96 tabbed renderers with 3 generated pages each and a 1 MiB synthetic payload in each renderer `MauiContext`. The expected current MAUI result is `RESULT: PROVEN` with 288 retained generated child subscriptions, 96 retained disposed renderers, and 96.0 MiB of retained context payload. Results are written to `/tmp/ios-tabbedrenderer-itemssource-reset-subscription-retention-results.txt`.
