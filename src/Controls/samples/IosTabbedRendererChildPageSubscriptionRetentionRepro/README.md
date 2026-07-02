# iOS TabbedRenderer child page subscription retention repro

This Mac Catalyst repro checks whether app-retained `TabbedPage` child pages keep disposed current Controls compatibility `TabbedRenderer` peers alive through stale child `Page.PropertyChanged` subscriptions.

It compares current MAUI with a control that removes only the renderer's child page subscriptions before renderer disconnect/dispose. Both runs retain the same child pages and disconnect the child page handlers so the measured retained graph is the disposed renderer and old `MauiContext` service graph, not live child page handlers.
