# Compatibility Layout Children Collection Retention Repro

This repro demonstrates that retaining empty public `Children` collection wrappers from legacy compatibility layouts can keep discarded layout owners alive.

The sample covers `Microsoft.Maui.Controls.Compatibility.StackLayout`, `FlexLayout`, `Grid`, `AbsoluteLayout`, and `RelativeLayout`. Each layout exposes a public `Children` wrapper over its internal child collection. The base compatibility `Layout` subscribes to `InternalChildren.CollectionChanged`, and the specialized `Grid`/`AbsoluteLayout`/`RelativeLayout` wrappers also keep a direct `Parent` reference for constraint helpers.

The sample creates 32 owners for each layout type. Each owner has a 1 MiB `BindingContext` payload, three child views are added and removed individually, and then only the empty public `Children` wrapper is retained by the app cache.

The control keeps the same empty wrappers but clears wrapper `Parent` fields and reachable collection event fields before forcing GC. The current run keeps MAUI's owner links intact.

Run:

```bash
dotnet build src/Controls/samples/CompatLayoutChildrenCollectionRetentionRepro/CompatLayoutChildrenCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/CompatLayoutChildrenCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/CompatLayoutChildrenCollectionRetentionRepro.app
cat /tmp/compat-layout-children-collection-retention-results.txt
```

Expected unpatched result:

```text
RESULT: PROVEN
...
Run: control: retained empty Children wrappers after clearing wrapper parent fields and collection event fields
  owners alive after full GC: 0/160
...
Run: current: retained empty Children wrappers with MAUI owner links intact
  owners alive after full GC: 160/160
  owner payload buffers alive after full GC: 160/160
  retained payload bytes: 160.0 MiB
```
