# AndroidItemsViewRendererElementRetentionLeakRepro

This repro exercises Android compatibility `CollectionViewRenderer` disposal through the shared `ItemsViewRenderer<TItemsView, TAdapter, TItemsViewSource>` base.

The control path disposes each native renderer and then clears the stale renderer-owned `ItemsView`, subclass `_itemsView`, and disposed adapter fields by reflection. The current path keeps disposed native renderers alive without clearing those fields. If Android or app code retains disposed native renderer peers, those fields keep disconnected `CollectionView` graphs and their payloads alive.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androiditemsviewrendererelementretentionleakrepro cat files/autorun-results.txt
```
