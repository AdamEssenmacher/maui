# Android SwitchCellView Cell Retention Repro

This repro exercises the legacy Android `SwitchCellRenderer` path and keeps only native `SwitchCellView` row peers alive after renderer disconnect.

Each cycle gives a `SwitchCell` a 1 MiB binding payload. The repro clears the generic `BaseCellView._cell` back-reference and stale native text fields in both runs. The control run also clears the SwitchCell-specific `SwitchCellView.Cell` property; current MAUI leaves it assigned, retaining the old cell and its binding payload.

Run with:

```sh
dotnet build src/Controls/samples/AndroidSwitchCellViewCellRetentionRepro/AndroidSwitchCellViewCellRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

The app writes its autorun result to `files/autorun-results.txt`.
