# Android Platform View Owner Retention Leak Repro

This repro proves that several current Android handlers keep disconnected virtual view graphs alive through stale platform-view owner fields when the native platform views remain rooted after handler disconnect.

The app creates platform views for current `Layout`, `Border`, `ScrollView`, `RefreshView`, and `SwipeView` handlers. Each virtual view carries a 1 MiB payload, then the handler is disconnected and only the native platform view is retained. The control run clears owner fields such as `CrossPlatformLayout` and `MauiSwipeView.Element` by reflection after disconnect. The current run leaves MAUI behavior unchanged.
