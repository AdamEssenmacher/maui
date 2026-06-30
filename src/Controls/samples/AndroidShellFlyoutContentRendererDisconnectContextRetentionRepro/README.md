# Android Shell Flyout Content Renderer Disconnect Context Retention Repro

This repro exercises the Android compatibility Shell flyout disconnect path. `ShellFlyoutRenderer.Disconnect()` clears the parent renderer's `_shellContext`, but it only calls `ShellFlyoutTemplatedContentRenderer.Disconnect()` on the child flyout content renderer. The child disconnect path detaches events and hosted child views but leaves its private `_shellContext` field assigned.

The repro retains disconnected `ShellFlyoutRenderer` parent peers in both scenarios to model delayed native peer cleanup. The control run clears only the child `ShellFlyoutTemplatedContentRenderer._shellContext` field after the real disconnect path. The current run leaves MAUI's current cleanup unchanged. Each Shell context carries a 1 MiB payload-backed `MauiContext` service graph so retained service-provider severity is visible.
