# iOS TabbedRenderer MauiContext retention repro

This Mac Catalyst repro checks whether retained disposed current Controls compatibility `TabbedRenderer` peers keep old window-scoped `MauiContext` service graphs alive after their virtual `TabbedPage` owners collect.

It compares current MAUI with a control that clears only `TabbedRenderer._mauiContext` after renderer disconnect/dispose while retaining the same renderer peers.
