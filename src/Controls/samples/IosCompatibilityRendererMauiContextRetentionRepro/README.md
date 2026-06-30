# iOS compatibility renderer MauiContext retention repro

This Mac Catalyst repro checks whether retained disposed current Controls compatibility renderer peers keep old window-scoped `MauiContext` service graphs alive after their virtual `FlyoutPage` owners collect.

It compares current MAUI with a control that clears only `PhoneFlyoutPageRenderer._mauiContext` after renderer disconnect/dispose while retaining the same renderer peers.
