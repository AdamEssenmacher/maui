# DataTemplateSelectorItemTypeCacheRetentionRepro

This repro targets `Microsoft.Maui.Controls.DataTemplateSelector._dataTemplates`.

When the container is a `ListView` using `ListViewCachingStrategy.RecycleElementAndDataTemplate`, `DataTemplateSelector.SelectTemplate(...)` caches the selected `DataTemplate` by `item.GetType()` in the selector instance. That cache has no eviction path. If a long-lived selector sees many collectible, plugin-provided, tenant-specific, or generated item types, the selector can retain every concrete item type for as long as the selector lives.

The repro generates 80 collectible dynamic item types. Each dynamic type carries a 1 MiB static payload to model realistic plugin, tenant, form-builder, media-catalog, or dashboard modules that keep generated static metadata and cached data beside their item types. A single retained selector selects a recyclable `Label` template for each generated item through the real `DataTemplateSelector.SelectTemplate(...)` path.

The sample uses an uninitialized `ListView` with `CachingStrategy` set to `RecycleElementAndDataTemplate` so the cache condition is exercised without requiring a platform dispatcher in this headless console repro. The selector code path under test is the production `SelectTemplate(...)` implementation.

Two scenarios are compared:

1. `Control`: clears only `DataTemplateSelector._dataTemplates` before forced GC.
2. `Current MAUI`: leaves `_dataTemplates` intact.

Run:

```bash
dotnet run --project src/Controls/samples/DataTemplateSelectorItemTypeCacheRetentionRepro/DataTemplateSelectorItemTypeCacheRetentionRepro.csproj -c Release -- --results=/tmp/datatemplateselector-itemtype-cache-retention-results.txt
```

Expected failing/current result:

```text
Control: explicit _dataTemplates.Clear()
  Selector cache entries: 0
  Retained types: 0/80
  Retained payloads: 0/80

Current MAUI: _dataTemplates left intact
  Selector cache entries: 80
  Retained types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
```

The control demonstrates that the generated types and payloads collect when the selector item-type cache is cleared. The current run demonstrates that the selector cache alone keeps the concrete item types, their collectible assemblies, and their static payloads alive.

This is distinct from Android `ItemsViewAdapter._viewTypeDataTemplates` cache retention: that cache lives in the Android adapter after `ItemTemplate` replacement, while this repro proves retention inside the cross-platform `DataTemplateSelector` instance itself.
