# SelectedItemsLeakRepro

This sample reproduces the `SelectionList` leak caused when a `CollectionView`
binds `SelectedItems` to a long-lived `ObservableCollection<object>`.

The repro models a real customer-review workflow:

- each pushed page shows a 600-row renewal batch;
- 40 customer records are preselected and saved in an app-level selection store;
- each page view model owns a 4 MB payload representing real page state such as cached workflow data, validation results, and local document buffers;
- the dashboard pushes and pops 25 pages, forcing full GC during and after the run.

With the defaults, the leaky scenario allocates 100 MB of page payload that should
be reclaimable after navigation. When the leak is present, the retained
`ObservableCollection<object>` keeps each `SelectionList` alive through its
`CollectionChanged` subscription, and each `SelectionList` keeps the popped
`CollectionView`, page, and view model alive.

## Build

From the repository root:

```sh
dotnet build src/Controls/samples/SelectedItemsLeakRepro/SelectedItemsLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/SelectedItemsLeakRepro/SelectedItemsLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
dotnet build src/Controls/samples/SelectedItemsLeakRepro/SelectedItemsLeakRepro.csproj -f net10.0-maccatalyst
```

## Run

```sh
dotnet run --project src/Controls/samples/SelectedItemsLeakRepro/SelectedItemsLeakRepro.csproj -f net10.0-maccatalyst
```

Run the scenarios in this order:

1. `Run retained List control`
2. `Run page-scoped Observable control`
3. `Run leaky ObservableCollection`

The retained `List<object>` control keeps comparable selected customer state but
does not implement `INotifyCollectionChanged`, so `SelectionList` does not
subscribe to it. The page-scoped `ObservableCollection<object>` control uses an
observable selection collection but does not keep that collection alive after the
page is popped. Both controls should drop popped pages after full GC.

The leaky scenario should report live weak references for most or all popped
pages, `CollectionView`s, page view models, and `SelectionList` wrappers. The
dashboard also reports the amount of page payload retained by live view models,
making the impact visible in MB instead of only object counts.

Use `Clear retained state` after a leaky run. If the retained selection store was
the only long-lived root, clearing it should allow a later full GC to release the
popped pages and their payloads.
