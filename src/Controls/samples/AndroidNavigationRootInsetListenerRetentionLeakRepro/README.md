# Android NavigationRoot inset listener retention repro

This repro exercises Android `NavigationRootManager.Connect()` when a window root is replaced. `Connect()` creates a `CoordinatorLayout` and registers it with `MauiWindowInsetListener.SetupViewWithLocalListener`, but the next `Connect()` starts with `ClearPlatformParts()` and drops `_managedCoordinatorLayout` without first calling `RemoveViewWithLocalListener`.

The current run creates 80 short-lived navigation roots with tracked safe-area payload views. The control run removes the local inset listener before replacement. The current run models MAUI's replacement path and proves the static inset-listener registry can retain the old payload views.
