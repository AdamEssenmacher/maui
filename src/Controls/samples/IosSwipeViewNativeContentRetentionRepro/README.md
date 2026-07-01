# iOS/Mac Catalyst SwipeView Native Content Retention Repro

This repro tests whether `SwipeViewHandler.DisconnectHandler()` leaves the current native content subtree attached to a retained native `MauiSwipeView` peer through both the native subview tree and the private `_contentView` field.

The control scenario retains the same native swipe peers but explicitly clears native subviews and `_contentView` after handler disconnect. The current MAUI scenario retains only native swipe peers after normal handler disconnect. Each `SwipeView.Content` child is a `Label` with a generated 256 KiB text payload, making the retained native `UILabel` payload measurable.

Run:

```bash
dotnet run --project src/Controls/samples/IosSwipeViewNativeContentRetentionRepro/IosSwipeViewNativeContentRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The app writes results to:

```text
/tmp/ios-swipeview-native-content-retention-results.txt
```
