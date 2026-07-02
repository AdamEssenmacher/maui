# ScrollViewRenderer Pending ScrollTo Retention Repro

This sample checks whether an offscreen iOS/Mac Catalyst compatibility `ScrollViewRenderer` can retain removed target payloads through its private `_requestedScroll` field.

The repro keeps `80` legacy `ScrollViewRenderer` instances alive. Each renderer is attached to a `ScrollView` with a real modern handler, receives `ScrollToAsync(target, ...)` while the legacy renderer's native view has no superview, removes the target content, disconnects the modern handler, and then forces full GC. The control path clears only the legacy renderer pending request field after content removal. The current path leaves MAUI's private pending request intact.

Each removed target carries a touched `1 MiB` payload buffer. Full retention therefore proves `80 MiB` of managed payload held by compatibility renderer pending scroll requests, while separately reporting the core `ScrollView` pending fields to distinguish this from the handlerless `ScrollView` pending-scroll leak.
