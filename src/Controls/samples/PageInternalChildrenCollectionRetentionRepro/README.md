# PageInternalChildrenCollectionRetentionRepro

This repro targets `Page.InternalChildren`, a public `[EditorBrowsable(Never)]` collection surface used internally by MAUI.

`Page` creates `InternalChildren` and subscribes an owner instance method in its constructor:

```text
InternalChildren.CollectionChanged += InternalChildrenOnCollectionChanged
```

App or framework helper code that keeps the collection after discarding the `Page` can root the old page through:

```text
app collection cache -> Page.InternalChildren.CollectionChanged -> Page.InternalChildrenOnCollectionChanged -> discarded Page
```

The sample adds child views, removes them one by one, and then retains only the empty `InternalChildren` collection. This avoids the already tracked `MultiPage<T>.ItemsSource` reset cleanup leak and the `TabbedPage.Children` wrapper leak. The control run keeps the same collections alive but clears retained collection event fields first.

## Run

```bash
dotnet build src/Controls/samples/PageInternalChildrenCollectionRetentionRepro/PageInternalChildrenCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/PageInternalChildrenCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/PageInternalChildrenCollectionRetentionRepro.app --args --results=/tmp/page-internalchildren-collection-retention-results.txt
```

The control run should retain zero `ContentPage` owners and payloads after full GC. The current run should retain every discarded page and its 1 MiB binding payload through the app-retained `InternalChildren` collections.
