# ListView TemplatedItems Retention Repro

This sample proves that retaining `ListView.TemplatedItems` can retain discarded `ListView` owners.

`ItemsView<T>.TemplatedItems` is public but marked for internal use. The returned `TemplatedItemsList` stores the owning items view in a private `_itemsView` field and subscribes to the owner's `PropertyChanged` event. The sample keeps only those list handles in an app cache and compares current MAUI against a control that clears the private owner field by reflection while keeping the same handles alive.
