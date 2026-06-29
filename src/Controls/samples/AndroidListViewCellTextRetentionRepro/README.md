# Android ListView Cell Text Retention Repro

This repro exercises the legacy Android `TextCellRenderer` path and keeps only native `BaseCellView` row peers alive after renderer disconnect.

Each cycle assigns a generated 128 KiB main text string and a generated 128 KiB detail text string to a `TextCell`. The repro clears the managed cell values and the known C107 `BaseCellView._cell` back-reference in both runs. The control run also clears `BaseCellView._mainTextText`, `BaseCellView._detailTextText`, and the child native `TextView.Text` slots. Current MAUI leaves those text slots assigned.

Run with:

```sh
dotnet build src/Controls/samples/AndroidListViewCellTextRetentionRepro/AndroidListViewCellTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

The app writes its autorun result to `files/autorun-results.txt`.
