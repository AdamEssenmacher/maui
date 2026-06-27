# ListView Pending ScrollTo Retention Repro

This sample proves that a live, handlerless `ListView` can retain removed item payloads through `_pendingScroll`.

The repro keeps `80` detached `ListView` instances alive. Each list view sets a one-item `ItemsSource`, creates a `ScrollTo(item, ...)` request while it has no platform, removes the item source, and then forces full GC. The control path clears the pending request field after source removal. The current path leaves MAUI's private pending request intact.

Each removed item carries a touched `1 MiB` payload buffer, so full retention proves `80 MiB` of managed payload held by pending scroll requests.
