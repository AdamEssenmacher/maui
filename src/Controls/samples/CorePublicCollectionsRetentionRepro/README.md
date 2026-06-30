# Core Public Collections Retention Repro

This sample proves that several owner-created public collection handles can retain discarded owners:

- `Page.ToolbarItems`
- `Page.MenuBarItems`
- `Cell.ContextActions`
- `Picker.Items`
- `FormattedString.Spans`
- `ResourceDictionary.MergedDictionaries`
- `Element.Effects`

The repro creates 24 owners for each surface with 1 MiB owner payloads. It adds and individually removes realistic child items for the mutable collections before retaining the public collection handle, so reset-cleanup classes are not part of the proof. `Element.Effects` is retained empty because simply accessing the property creates a `TrackableCollection` and subscribes the owner to its events.

Current MAUI keeps the owners alive through `CollectionChanged` or related collection event fields. The control run reflectively clears the retained collection event fields before retaining the same collection handles.

Expected result:

```text
RESULT: PROVEN
control: 0/168 owners, payloads, and payload buffers retained
current: 168/168 owners, payloads, and payload buffers retained
```

Run:

```bash
dotnet build src/Controls/samples/CorePublicCollectionsRetentionRepro/CorePublicCollectionsRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/CorePublicCollectionsRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/CorePublicCollectionsRetentionRepro.app
cat /tmp/core-public-collections-retention-results.txt
```
