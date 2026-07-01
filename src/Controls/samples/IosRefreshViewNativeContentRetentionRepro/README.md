# iOS/Mac Catalyst RefreshView Native Content Retention Repro

This repro tests whether `RefreshViewHandler.DisconnectHandler()` leaves the current native content subtree attached to, and assigned inside, a retained native `MauiRefreshView` peer.

The control scenario retains the same native `MauiRefreshView` peers but explicitly removes native subviews and clears the private `_contentView` field after handler disconnect. The current MAUI scenario retains only native parent peers after normal handler disconnect. Each `RefreshView.Content` child is a `Label` with a generated 256 KiB text payload, making the retained native `UILabel` payload measurable.

Run:

```bash
dotnet run --project src/Controls/samples/IosRefreshViewNativeContentRetentionRepro/IosRefreshViewNativeContentRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The app writes results to:

```text
/tmp/ios-refreshview-native-content-retention-results.txt
```
