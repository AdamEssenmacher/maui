# Android MauiRecyclerView Element Retention Leak Repro

This repro proves that the current Android `MauiRecyclerView` used by `CollectionViewHandler` keeps stale references to the disconnected `CollectionView` graph after handler disconnect when the native RecyclerView remains rooted.

The app creates 80 current-handler `CollectionView` instances with 1 MiB payloads, disconnects their handlers, and retains only the native RecyclerView peers. The control run clears the stale `MauiRecyclerView` fields by reflection after disconnect. The current run leaves MAUI behavior unchanged.
