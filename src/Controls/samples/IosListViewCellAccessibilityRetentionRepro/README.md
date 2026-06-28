# iOS ListView Cell Accessibility Retention Repro

This repro proves that retained iOS/Mac Catalyst legacy `ListView` native cell peers keep their last accessibility label and hint payload after MAUI cell objects are gone.

Static path:

- `src/Controls/src/Core/Compatibility/Handlers/ListView/iOS/CellRenderer.cs` maps `AutomationProperties.Name` and `HelpText` into `UITableViewCell.AccessibilityLabel` and `AccessibilityHint`.
- `CellTableViewCell.Cell` is weak and disposal clears only the weak cell reference/attached real-cell mapping, not retained native accessibility text slots.

Run:

```bash
dotnet build src/Controls/samples/IosListViewCellAccessibilityRetentionRepro/IosListViewCellAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosListViewCellAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosListViewCellAccessibilityRetentionRepro.app/Contents/MacOS/IosListViewCellAccessibilityRetentionRepro
```

The result file is written to `/tmp/ios-listview-cell-accessibility-retention-results.txt`.
