# Android EntryCellView Delegate Retention Repro

This repro exercises the legacy Android `EntryCellRenderer` path and keeps only native `EntryCellView` row peers alive after renderer disconnect.

Each cycle gives the renderer a fresh `MauiContext` containing a 512 KiB service-provider payload and an `EntryCell.BindingContext` containing another 512 KiB payload. The repro clears the known native `_cell` back-reference and native text slots in both runs. The control run also clears `EntryCellView.TextChanged` and `EntryCellView.EditingCompleted`; current MAUI leaves those delegates assigned, retaining the disconnected renderer, its `MauiContext`, and its `Cell` payload graph.

Run with:

```sh
dotnet build src/Controls/samples/AndroidEntryCellViewDelegateRetentionRepro/AndroidEntryCellViewDelegateRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

The app writes its autorun result to `files/autorun-results.txt`.
