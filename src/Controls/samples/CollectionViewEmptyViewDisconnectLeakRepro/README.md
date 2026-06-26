# CollectionView EmptyView Disconnect Leak Repro

This repro exercises iOS/Mac Catalyst `ItemsViewController.TearDownEmptyView()` and the equivalent `ItemsViewController2` path.

The app repeatedly replaces `CollectionView.EmptyView` with retained MAUI views while the collection is empty. The control path explicitly disconnects the old empty view handler before replacement. The current MAUI path removes the old empty view from the logical tree and clears controller fields, but does not disconnect the old handler.

Expected autorun result:

```text
RESULT: PROVEN
control-explicit-emptyview-disconnect: ... payloads=0/80
leak-current-emptyview-replacement: ... payloads=80/80, retainedMiB=80.0
```
