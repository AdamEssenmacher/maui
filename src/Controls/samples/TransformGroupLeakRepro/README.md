# TransformGroupLeakRepro

This repro targets `TransformGroup.Children`. When a child transform is added to
a `TransformGroup`, the group subscribes to the child transform's
`PropertyChanged` event. Replacing the entire `Children` collection unsubscribes
from the old collection's `CollectionChanged` event, but it does not unsubscribe
from the child transforms already in that collection.

If those child transforms are long-lived, such as resources, static fields,
singletons, or cached visual state, they can retain closed `TransformGroup`
instances. A retained group can retain its `Path` through the path's
`RenderTransform` subscription, and that path can retain a realistic cached
view-model payload through `BindingContext`.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/TransformGroupLeakRepro/TransformGroupLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/TransformGroupLeakRepro/TransformGroupLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/TransformGroupLeakRepro/TransformGroupLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/TransformGroupLeakRepro/TransformGroupLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst and write a result file:

```bash
TRANSFORM_GROUP_LEAK_REPRO_AUTORUN=1 \
TRANSFORM_GROUP_LEAK_REPRO_RESULTS=/private/tmp/transformgroupleakrepro-results.txt \
dotnet run --project src/Controls/samples/TransformGroupLeakRepro/TransformGroupLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/transformgroupleakrepro-results.txt
```

If the Mac Catalyst app sandbox cannot write the requested path, the app falls
back to its local application data container and prints the path it used.

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/TransformGroupLeakRepro/TransformGroupLeakRepro.csproj -f net10.0-android -p:TransformGroupLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.transformgroupleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

## What to Check

Use the default settings first:

- Pages/run: `40`
- Paths/page: `12`
- Payload MB/page: `3`
- Dwell ms/page: `25`

Run these scenarios:

1. `Run control`
   - Pushes and pops vector-heavy Shell pages.
   - Each `TransformGroup` uses a private child `ScaleTransform` before replacing `Children`.
   - After full GC, alive pages, paths, transform groups, and payload view models should stay near zero.

2. `Run shared transform`
   - Each `TransformGroup` adds a long-lived shared child `ScaleTransform`, then replaces `Children`.
   - On an unpatched build, alive paths, transform groups, and payload view models should grow with the page count after full GC.
   - `Payload retained by alive view models` is the clearest real-world impact number. With defaults, an unpatched build can retain about `120 MB` of view-model payload.

3. `Run mitigation`
   - Uses the same shared child transforms, but removes each child from `Children` before replacing the collection.
   - Counts should return close to the control run. This demonstrates that the stale child transform event subscription is the retention root.

The app forces full GC before measurements so retained weak references are
meaningful. It also reports managed heap, GC heap, resident memory, and
working-set deltas after collection.

## Observed Mac Catalyst Run

On an unpatched local build, the default autorun produced:

```text
Run: control: private child ScaleTransform per Path
Weak refs still alive after full GC:
  pages: 0/40
  Paths: 0/480
  TransformGroups: 0/480
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)

Run: leaky shared child ScaleTransform
Weak refs still alive after full GC:
  pages: 0/40
  Paths: 480/480
  TransformGroups: 480/480
  payload view models: 40/40
Payload retained by alive view models: 120.0 MB (100.0% of allocated payload)

Run: mitigation: remove shared child before replacing Children
Weak refs still alive after full GC:
  pages: 0/40
  Paths: 0/480
  TransformGroups: 0/480
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)
```
