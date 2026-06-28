# ItemsViewLayout ItemsLayout Retention Repro

This repro demonstrates that a retained disposed iOS/Mac Catalyst `GridViewLayout` keeps its MAUI `GridItemsLayout` alive through stale private fields.

The control path disposes each native layout and then reflectively clears the stale base and derived `_itemsLayout` fields. The current MAUI path disposes each native layout but leaves both fields assigned. Each retained `GridItemsLayout` carries a realistic 1 MiB layout-state payload.

Run:

```bash
dotnet run --project src/Controls/samples/ItemsViewLayoutItemsLayoutRetentionRepro/ItemsViewLayoutItemsLayoutRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes results to the process temp directory as `itemsviewlayout-itemslayout-retention-results.txt`.
