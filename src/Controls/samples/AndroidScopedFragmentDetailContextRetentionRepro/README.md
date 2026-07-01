# AndroidScopedFragmentDetailContextRetentionRepro

This repro isolates Android core `Microsoft.Maui.Platform.ScopedFragment` destroyed-fragment field retention.

`ScopedFragment` is internal, so the sample creates the real MAUI type by reflection. The app runs automatically on startup and writes `autorun-results.txt` into app data. It creates 96 destroyed fragment peers per scenario, with each fragment carrying a `BoxView` detail view and a synthetic `MauiContext` that resolves a 1 MiB payload service. The hosted view handler is disconnected in both scenarios before GC.

The control run reflection-clears only `ScopedFragment.DetailView` and `_mauiContext` after `OnDestroy()`. The current-MAUI run leaves those fields assigned. A proved run shows the retained fragment peers stay alive in both scenarios, the control detail/context graph collects, and the current-MAUI detail/context graph remains alive through the destroyed fragment fields.
