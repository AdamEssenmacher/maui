# MultiPageChildrenCollectionRetentionRepro

This repro targets `TabbedPage.Children`, the public `MultiPage<Page>.Children` collection surface in this source tree.

`MultiPage<T>` exposes an `ElementCollection<T>` wrapper over `Page.InternalChildren`. The constructor subscribes `InternalChildren.CollectionChanged += OnChildrenChanged`, so app code that keeps the `Children` collection wrapper after discarding the `TabbedPage` can root the old page through:

```text
app collection cache -> TabbedPage.Children wrapper -> InternalChildren.CollectionChanged -> MultiPage.OnChildrenChanged -> discarded TabbedPage
```

The sample adds child pages, removes them one by one, and then retains only the empty `Children` wrapper. This avoids the already tracked `ItemsSource.Clear()`/reset logical-child leak. The control run keeps the same wrappers alive but clears the retained collection event fields first.

## Run

```bash
dotnet build src/Controls/samples/MultiPageChildrenCollectionRetentionRepro/MultiPageChildrenCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/MultiPageChildrenCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MultiPageChildrenCollectionRetentionRepro.app --args --results=/tmp/multipage-children-collection-retention-results.txt
```

The control run should retain zero `TabbedPage` owners and payloads after full GC. The current run should retain every discarded owner and its 1 MiB binding payload through the app-retained `Children` wrappers.
