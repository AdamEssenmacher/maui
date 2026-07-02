# Android TabbedPageManager ItemsSource Reset Subscription Retention Repro

This sample proves that Android `TabbedPageManager` leaves generated `TabbedPage` child pages subscribed after an `ItemsSource` reset.

The repro creates transient bottom-tab `TabbedPage` managers whose children are generated from `ItemsSource` and `ItemTemplate`, retains those generated child pages to model a page cache or offscreen composition surface, clears the backing item source to force the `MultiPage<T>` reset path, neutralizes the already-cataloged stale logical-child reset root and Android manager-field root, and disconnects the manager. The control run explicitly calls `TabbedPageManager.TeardownPage(...)` for the generated pages before `ItemsSource.Clear()`. The current run uses MAUI's cleanup path as-is.

The expected result is that both runs retain the same generated pages, but only current MAUI keeps the generated page `PropertyChanged` subscriptions pointing at disposed `TabbedPageManager` instances. Those retained managers keep their `MauiContext` and synthetic service-provider payload alive.
