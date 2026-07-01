# iOS/Mac Catalyst ScrollView Native Content Retention Repro

This repro tests whether `ScrollViewHandler.DisconnectHandler()` leaves the current native content subtree attached to a retained native `MauiScrollView` peer.

The control scenario retains the same native scroll peers but explicitly clears native subviews after handler disconnect. The current MAUI scenario retains only native scroll peers after normal handler disconnect. Each `ScrollView.Content` child is a `Label` with a generated 256 KiB text payload, making the retained native `UILabel` payload measurable.

Run:

```bash
dotnet run --project src/Controls/samples/IosScrollViewNativeContentRetentionRepro/IosScrollViewNativeContentRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The app writes results to:

```text
/tmp/ios-scrollview-native-content-retention-results.txt
```
