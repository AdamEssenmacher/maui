# MapElements Clear Retention Leak Repro

This repro proves that `Map.MapElements.Clear()` can leave removed `MapElement` instances subscribed to their old `Map`, retaining map payload graphs when app code keeps the removed elements alive.

The control path removes elements one by one, which gives `MapElementsCollectionChanged` old items and detaches `MapElement.PropertyChanged`. The current path calls `MapElements.Clear()`, which raises a reset without old items, so the detach path is skipped.
