# iOS SwitchCell Native Text Retention Repro

This repro proves that retained iOS/Mac Catalyst legacy `ListView` `SwitchCell` native cell peers keep their last native label text payload after MAUI `SwitchCell` objects are gone.

Static path:

- `src/Controls/src/Core/Compatibility/Handlers/ListView/iOS/SwitchCellRenderer.cs` assigns `SwitchCell.Text` into `UITableViewCell.TextLabel.Text`.
- `CellTableViewCell.Cell` is weak and disposal clears only the weak cell reference/attached real-cell mapping, not the retained native label text.
- This is the SwitchCell sibling of C235, which covered TextCell/EntryCell and explicitly left SwitchCell out because the earlier harness did not parent the cell for flow-direction setup.

Run:

```bash
dotnet build src/Controls/samples/IosSwitchCellNativeTextRetentionRepro/IosSwitchCellNativeTextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosSwitchCellNativeTextRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosSwitchCellNativeTextRetentionRepro.app/Contents/MacOS/IosSwitchCellNativeTextRetentionRepro
```

The result file is written to `/tmp/ios-switchcell-native-text-retention-results.txt`.
