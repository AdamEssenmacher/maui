# iOS ListView Cell Text Retention Repro

This repro proves that retained iOS/Mac Catalyst legacy `ListView` native cell peers keep their last native text payload after MAUI cell objects are gone.

Static paths:

- `src/Controls/src/Core/Compatibility/Handlers/ListView/iOS/TextCellRenderer.cs` assigns `TextCell.Text` and `Detail` into `UITableViewCell.TextLabel.Text` and `DetailTextLabel.Text`.
- `src/Controls/src/Core/Compatibility/Handlers/ListView/iOS/EntryCellRenderer.cs` assigns `EntryCell.Label`, `Text`, and `Placeholder` into native cell label/text-field slots.
- `CellTableViewCell.Cell` is weak and disposal clears only the weak cell reference/attached real-cell mapping, not the retained native text fields.

Run:

```bash
dotnet build src/Controls/samples/IosListViewCellTextRetentionRepro/IosListViewCellTextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosListViewCellTextRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosListViewCellTextRetentionRepro.app/Contents/MacOS/IosListViewCellTextRetentionRepro
```

The result file is written to `/tmp/ios-listview-cell-text-retention-results.txt`.
