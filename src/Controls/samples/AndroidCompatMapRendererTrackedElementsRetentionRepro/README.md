# Android compatibility MapRenderer tracked-elements retention repro

This sample proves that the legacy Android compatibility `MapRenderer` can retain removed map overlay models for as long as the renderer stays alive.

`MapRenderer.AddMapElements()` appends every added `MapElement` to the private `_trackedMapElements` list. The individual remove path detaches `MapElement.PropertyChanged` and removes the native overlay, but it does not remove the model from `_trackedMapElements` or clear the model's `MapElementId`. A long-lived map page that repeatedly replaces routes, tracks, geofences, or other overlays can therefore keep old overlay payloads alive even after the overlays were removed from the visible `Map.MapElements` collection.

The autorun keeps one compatibility `MapRenderer` alive in both scenarios, adds and individually removes 160 realistic `Polyline` overlay models with 512 KiB payloads, and compares current MAUI with a control that explicitly clears `_trackedMapElements` after removal. Current MAUI retains all 160 removed overlays and 80 MiB of payload. The cleanup control retains none.
