# Map ItemsSource Subscription Retention Repro

This repro proves that a long-lived `INotifyCollectionChanged` `Map.ItemsSource` can retain discarded `Map` instances through the map's strong `CollectionChanged` subscription.

The control path sets `ItemsSource = null` before dropping each map, which removes the subscription. The current path leaves the shared source assigned and then drops the map. Because `Map` has no disposal path that clears `ItemsSource`, the shared source keeps every discarded map alive.
