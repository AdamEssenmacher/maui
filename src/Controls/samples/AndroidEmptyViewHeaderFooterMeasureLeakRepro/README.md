# Android EmptyView Header/Footer Measurement Leak Repro

This repro exercises Android `EmptyViewAdapter.UpdateHeaderFooterHeight`.

The app keeps a non-empty `CollectionView` with an `EmptyView` configured. Each iteration assigns a new off-screen footer view and changes `EmptyView`, which refreshes the hidden empty adapter. The empty adapter measures the footer by creating a handler through `TemplateHelpers.GetHandler`, even though the footer is not realized by the active adapter. The control path explicitly disconnects that measured footer handler; the current path leaves it attached to the retained footer view.

Expected autorun result:

```text
RESULT: PROVEN
control-explicit-footer-disconnect: ... payloads=0/80
leak-current-emptyview-footer-measurement: ... payloads=80/80, retainedMiB=80.0
```
