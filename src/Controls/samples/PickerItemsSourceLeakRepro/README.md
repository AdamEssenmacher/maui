# PickerItemsSourceLeakRepro

This repro targets `Picker.ItemsSource`. `Picker` subscribes to
`INotifyCollectionChanged.CollectionChanged` when `ItemsSource` is set and only
unsubscribes when the property changes. A long-lived `ObservableCollection` can
therefore retain closed pickers assigned to it.

The sample models real form pages that reuse one shared choices collection,
such as region, warehouse, category, or status options. Each closed page carries
a view-model payload to make retention severity easy to measure.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/PickerItemsSourceLeakRepro/PickerItemsSourceLeakRepro.csproj -f net10.0-maccatalyst
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/PickerItemsSourceLeakRepro/PickerItemsSourceLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst and write a result file:

```bash
PICKER_ITEMSSOURCE_LEAK_REPRO_AUTORUN=1 \
PICKER_ITEMSSOURCE_LEAK_REPRO_RESULTS=/private/tmp/pickeritemssourceleakrepro-results.txt \
dotnet run --project src/Controls/samples/PickerItemsSourceLeakRepro/PickerItemsSourceLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/pickeritemssourceleakrepro-results.txt
```

If `dotnet run` returns immediately after building the app bundle, run the
generated bundle executable directly with the same environment variables.

## What to Check

Use the default settings first:

- Pages/run: `60`
- Pickers/page: `6`
- Choices/picker: `80`
- Payload MB/page: `2`
- Dwell ms/page: `25`

Run these scenarios:

1. `Run control`
   - Pushes and pops the same Shell pages, but every `Picker` gets a fresh
     `ObservableCollection`.
   - After full GC, alive pages, pickers, and payload view models should stay
     near zero.

2. `Run shared source`
   - All pickers use the same long-lived `ObservableCollection`, matching a
     shared view-model property, singleton cache, or app-level choices service.
   - On an unpatched build, alive pickers and payload view models should grow
     with the page count after full GC.
   - `Payload retained by alive view models` is the clearest real-world impact
     number. With defaults, a full leak retains about `120 MB` of view-model
     payload plus the retained picker controls.

3. `Run mitigation`
   - Uses the same shared collection, but sets `Picker.ItemsSource = null` when
     each page disappears.
   - Counts should return close to the control run. This demonstrates that the
     shared `ItemsSource.CollectionChanged` subscription is the retention root.

The app forces full GC before measurements so retained weak references are
meaningful. It also reports managed heap, GC heap, resident memory, and
working-set deltas after collection.

## Observed Mac Catalyst Run

On an unpatched local build, the default autorun produced:

```text
Run: control: fresh ObservableCollection per Picker
Weak refs still alive after full GC:
  pages: 0/60
  Pickers: 0/360
  payload view models: 0/60
Payload retained by alive view models: 0 B (0.0% of allocated payload)

Run: leaky shared ObservableCollection ItemsSource
Weak refs still alive after full GC:
  pages: 0/60
  Pickers: 360/360
  payload view models: 60/60
Payload retained by alive view models: 120.0 MB (100.0% of allocated payload)

Run: mitigation: clear shared Picker.ItemsSource
Weak refs still alive after full GC:
  pages: 0/60
  Pickers: 0/360
  payload view models: 0/60
Payload retained by alive view models: 0 B (0.0% of allocated payload)
```
