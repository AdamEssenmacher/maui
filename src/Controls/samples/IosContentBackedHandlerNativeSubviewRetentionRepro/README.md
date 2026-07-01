# iOS/Mac Catalyst Content-Backed Handler Native Subview Retention Repro

This repro tests whether iOS/Mac Catalyst handlers that use `Microsoft.Maui.Platform.ContentView` as their native parent leave current presented native child views attached after handler disconnect.

The sample covers `RadioButtonHandler` and `SwipeItemViewHandler`. The control scenario retains the same native parent peers but calls `ClearSubviews()` after handler disconnect. The current MAUI scenario retains only native parent peers after normal handler disconnect. Each child `Label` maps a realistic generated 256 KiB text payload into a native `UILabel`, making the impact measurable when the native child subtree remains attached.

Run:

```bash
dotnet run --project src/Controls/samples/IosContentBackedHandlerNativeSubviewRetentionRepro/IosContentBackedHandlerNativeSubviewRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The app writes results to:

```text
/tmp/ios-content-backed-handler-native-subview-retention-results.txt
```
