# Android compatibility MapRenderer callback retention repro

This sample proves that the legacy Android compatibility `MapRenderer` can retain disposed map payloads when a native `MapView.GetMapAsync` queue still holds the renderer as its `IOnMapReadyCallback`.

The current `MapHandler` callback-retention path is already tracked separately by `AndroidMapReadyCallbackRetentionLeakRepro`. This repro isolates the compatibility renderer path: `MapRenderer` passes `this` to `GetMapAsync`, and `Dispose(bool)` does not clear the renderer's compatibility handler `Element`/`MauiContext` state.

The autorun keeps 80 disposed renderer callbacks in a simulated native-pending queue. The control explicitly clears the renderer's virtual view and `MauiContext` after dispose. Current MAUI leaves them assigned, retaining 80 map/context payloads of 1 MiB each.
