# Android ShellRenderer MauiContext Retention Repro

This repro exercises the Android compatibility `ShellRenderer` disconnect path. `ShellRenderer.DisconnectHandler()` removes Shell event subscriptions, disconnects the flyout and current item renderers, clears `_currentView`, and clears `Element`, but it does not clear the renderer's private `_mauiContext` field.

The repro retains disconnected `ShellRenderer` peers in both scenarios. After the real disconnect path, both runs clear child/native fields such as `_flyoutView` and `_frameLayout` to isolate this from Shell item, flyout content, and native-view roots. The control run additionally clears only `ShellRenderer._mauiContext`. Each context carries a 1 MiB payload-backed service graph to demonstrate retained service-provider severity.
