# ScrollView Pending ScrollTo Retention Repro

This sample proves that a live, handlerless `ScrollView` can retain removed target subtrees through `_pendingScrollToRequested`.

The repro keeps `80` detached `ScrollView` instances alive. Each scroll view creates a `ScrollToAsync(target, ...)` request while it has no handler, removes the target content, and then forces full GC. The control path clears the pending request field and cancels the pending scroll task before removal. The current path leaves MAUI's private pending request intact.

Each removed target carries a touched `1 MiB` payload buffer, so full retention proves `80 MiB` of managed payload held by pending scroll requests.
