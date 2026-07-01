# iOS/Mac Catalyst GeneralWrapperView Native Child Retention Repro

This repro tests whether internal `GeneralWrapperView.Disconnect()` leaves its native child subtree attached to a retained native wrapper peer.

The control scenario retains the same native wrapper peers but explicitly clears native subviews after `Disconnect()`. The current MAUI scenario retains only native wrapper peers after normal `GeneralWrapperView.Disconnect()`. Each child is a `Label` with a generated 256 KiB text payload, making the retained native `UILabel` payload measurable.

Run:

```bash
dotnet run --project src/Controls/samples/IosGeneralWrapperViewNativeChildRetentionRepro/IosGeneralWrapperViewNativeChildRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The app writes results to:

```text
/tmp/ios-generalwrapperview-native-child-retention-results.txt
```
