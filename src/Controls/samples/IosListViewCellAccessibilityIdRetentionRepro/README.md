# iOS ListView Cell Accessibility Identifier Retention Repro

This repro proves that retained iOS/Mac Catalyst legacy `ListView` native `TextCell` peers keep their last accessibility identifier payload after MAUI cell objects are gone.

Static path:

- `src/Controls/src/Core/Compatibility/Handlers/ListView/iOS/TextCellRenderer.cs` maps `TextCell.AutomationId` into `UITableViewCell.AccessibilityIdentifier`.
- `CellTableViewCell.Cell` is weak and disposal clears only the weak cell reference/attached real-cell mapping, not retained native accessibility identifier state.

Run:

```bash
dotnet build src/Controls/samples/IosListViewCellAccessibilityIdRetentionRepro/IosListViewCellAccessibilityIdRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosListViewCellAccessibilityIdRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosListViewCellAccessibilityIdRetentionRepro.app/Contents/MacOS/IosListViewCellAccessibilityIdRetentionRepro
```

The result file is written to `/tmp/ios-listview-cell-accessibilityid-retention-results.txt`.
