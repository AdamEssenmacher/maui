# CollectionView Header/Footer Disconnect Leak Repro

This repro exercises the older iOS/Mac Catalyst `StructuredItemsViewController.UpdateSubview()` header/footer path.

The app registers the older `CollectionViewHandler`, repeatedly replaces `CollectionView.Header` with retained MAUI views, and compares explicit old-header handler disconnect against current replacement. The current path removes the old header from the native and logical trees but overwrites the old handler references without disconnecting the old MAUI view handler.

Expected autorun result:

```text
RESULT: PROVEN
control-explicit-header-disconnect: ... payloads=0/80
leak-current-header-replacement: ... payloads=80/80, retainedMiB=80.0
```
