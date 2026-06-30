# Android Shell flyout content handler retention repro

This sample proves that `ShellFlyoutTemplatedContentRenderer.Disconnect()` can leave a disconnected hosted `FlyoutContent` handler retained through the internal `ShellViewRenderer.Handler` property.

The repro creates retained Android Shell flyout renderer peers with custom `Shell.FlyoutContent`. It forces MAUI's real `ShellFlyoutTemplatedContentRenderer.UpdateFlyoutContent()` path, calls the real parent `ShellFlyoutRenderer.Disconnect()` path, then keeps the disconnected parent renderers alive. Both runs clear already-tracked non-candidate fields: the child `ShellFlyoutTemplatedContentRenderer._shellContext` field from C357 and the nested `ShellViewRenderer._mauiContext` field from C352. The control run additionally clears only the nested `ShellViewRenderer.Handler` reference.

Each attempt gives the hosted content handler a per-attempt `MauiContext` with a 1 MiB payload service. Current MAUI retains those disconnected handlers and their service-provider payloads; the control releases them.
