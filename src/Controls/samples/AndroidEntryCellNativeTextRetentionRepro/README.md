# Android EntryCell Native Text Retention Repro

This repro exercises the legacy Android `EntryCellRenderer` path that copies `EntryCell.Label` and `EntryCell.Placeholder` into retained `EntryCellView` row state.

Each cycle uses 8 KiB generated values for the label and placeholder, keeps only native `EntryCellView` row peers alive after renderer disconnect, and clears the known native `_cell` back-reference plus `TextChanged`/`FocusChanged`/`EditingCompleted` delegates in both runs. The control also clears the label backing field, native label `Text`, `EditText.Text`, and `EditText.Hint`; current MAUI leaves the label backing field, native label `Text`, and `EditText.Hint` payload slots assigned.

Run with:

```sh
dotnet build src/Controls/samples/AndroidEntryCellNativeTextRetentionRepro/AndroidEntryCellNativeTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

The app writes its autorun result to `files/autorun-results.txt`.
