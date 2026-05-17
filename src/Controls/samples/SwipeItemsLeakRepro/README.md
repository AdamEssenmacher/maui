# SwipeItemsLeakRepro

This repro targets the shared `SwipeView` code path where `SwipeView.OnSwipeItemsChanged`
subscribes local-function handlers to `SwipeItems.CollectionChanged` and
`SwipeItems.PropertyChanged`. When the `SwipeItems` object is long-lived, those handlers retain
the old `SwipeView` and its inherited binding context after the page is popped.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/SwipeItemsLeakRepro/SwipeItemsLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/SwipeItemsLeakRepro/SwipeItemsLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
dotnet build src/Controls/samples/SwipeItemsLeakRepro/SwipeItemsLeakRepro.csproj -f net10.0-android
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/SwipeItemsLeakRepro/SwipeItemsLeakRepro.csproj -f net10.0-maccatalyst
```

## What to Check

Use the default settings first:

- Pages/run: `25`
- Swipe rows/page: `40`
- Payload KB/row: `128`
- Dwell ms/page: `40`

Run these scenarios:

1. `Run control`
   - Each `SwipeView` owns its own short-lived `SwipeItems`.
   - After full GC, alive `SwipeViews`, row view models, and board view models should stay near zero.

2. `Run cached SwipeItems`
   - Models a field-service app that caches row action menus for common actions like done, route, and hold.
   - The repro keeps each row's `SwipeItems` in a long-lived cache so the `SwipeItems` are realistic external roots.
   - On an unpatched build, alive `SwipeViews`, board view models, and row view models should grow with the page count after full GC.
   - `Payload retained by alive board view models` is the clearest real-world impact number. With defaults, an unpatched build retains about `125 MB` of row payload, plus the retained UI objects and handlers.

3. `Run replace RightItems`
   - Uses cached `SwipeItems`, then sets each row's `RightItems` to a new `SwipeItems` during page disappearance.
   - On an unpatched build, this still leaks because the unsubscribe creates new local-function delegates that do not match the subscribed delegates.
   - On a patched build, this should return close to the control run.

The app forces full GC before measurements so retained weak references are meaningful. It also reports managed heap, GC heap, resident memory, and working-set deltas after collection.
