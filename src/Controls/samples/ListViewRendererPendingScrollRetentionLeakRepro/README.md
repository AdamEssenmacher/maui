# ListViewRenderer Pending ScrollTo Retention Repro

This sample proves that an offscreen iOS/Mac Catalyst compatibility `ListViewRenderer` can retain removed item payloads through `_requestedScroll`.

The repro keeps `80` offscreen renderer handlers alive. Each renderer is created for a one-item `ListView`, receives a `ScrollTo(item, ...)` request while its native table has no superview, removes the item source, and then forces full GC. The control path clears the renderer pending request field after source removal. The current path leaves MAUI's private pending request intact.

Each removed item carries a touched `1 MiB` payload buffer, so full retention proves `80 MiB` of managed payload held by renderer pending scroll requests.
