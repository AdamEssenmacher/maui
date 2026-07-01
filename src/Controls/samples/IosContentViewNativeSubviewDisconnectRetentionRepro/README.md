# iOS/Mac Catalyst ContentView Native Subview Disconnect Retention Repro

This repro tests whether `ContentViewHandler.DisconnectHandler()` leaves the current presented native child view attached to a retained native `Microsoft.Maui.Platform.ContentView` peer.

The control scenario retains the same native parent peers but calls `ClearSubviews()` after handler disconnect. The current MAUI scenario retains only native parent peers after normal handler disconnect. Each child `Label` maps a realistic generated 256 KiB text payload into a native `UILabel`, making the impact measurable when the native child subtree remains attached.

Run:

```bash
dotnet run --project src/Controls/samples/IosContentViewNativeSubviewDisconnectRetentionRepro/IosContentViewNativeSubviewDisconnectRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The app writes results to:

```text
/tmp/ios-contentview-native-subview-disconnect-retention-results.txt
```
