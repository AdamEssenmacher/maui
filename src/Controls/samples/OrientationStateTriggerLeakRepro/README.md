# OrientationStateTriggerLeakRepro

This repro models a runtime tenant or brand theme refresh on visible
orientation-aware dashboard controls. The refresh replaces
`VisualStateManager.VisualStateGroups` on an attached card so the dashboard can
switch to a different set of visual states.

The leak is in the shared state-trigger replacement path.
`OrientationStateTrigger` subscribes to
`DeviceDisplay.MainDisplayInfoChanged` in `OnAttached()` and unsubscribes in
`OnDetached()`. If `VisualStateGroups` is replaced while the element is already
attached to a `Window`, the old triggers are not detached. Later, when the
element leaves the window, MAUI only detaches triggers from the current VSM
groups, so the old `OrientationStateTrigger` remains rooted by
`DeviceDisplay` and can retain trigger-local state such as an explicit
`BindingContext` or binding source.

Normal static XAML visual states are not expected to leak. The risky pattern is
runtime replacement of VSM groups on visible controls, such as tenant branding,
theme swapping, accessibility or density mode changes, or controls/templates
that rebuild state-trigger groups while on screen.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/OrientationStateTriggerLeakRepro/OrientationStateTriggerLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/OrientationStateTriggerLeakRepro/OrientationStateTriggerLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/OrientationStateTriggerLeakRepro/OrientationStateTriggerLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/OrientationStateTriggerLeakRepro/OrientationStateTriggerLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst and write a result file:

```bash
ORIENTATION_STATE_TRIGGER_LEAK_REPRO_AUTORUN=1 \
ORIENTATION_STATE_TRIGGER_LEAK_REPRO_RESULTS=/private/tmp/orientationstatetriggerleakrepro-results.txt \
dotnet run --project src/Controls/samples/OrientationStateTriggerLeakRepro/OrientationStateTriggerLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/orientationstatetriggerleakrepro-results.txt
```

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/OrientationStateTriggerLeakRepro/OrientationStateTriggerLeakRepro.csproj -f net10.0-android -p:OrientationStateTriggerLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.orientationstatetriggerleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

## What to Check

Use the default settings first:

- Pages/run: `40`
- Payload MB/page: `3`
- Dwell ms/page: `100`

Run these scenarios:

1. `Run control`
   - Each page sets `VisualStateGroups` with an `OrientationStateTrigger`
     once before the target view is attached.
   - After full GC, old triggers and payload view models should stay near zero.

2. `Run live VSM replacement`
   - Each page starts with the Contoso orientation theme, waits until the target
     view is loaded and has a `Window`, then applies the Northwind theme by
     replacing `VisualStateGroups`.
   - On an unpatched build, the old `OrientationStateTrigger` remains
     subscribed to `DeviceDisplay.MainDisplayInfoChanged` and retains the
     trigger-local payload view model.
   - `Payload retained by alive view models` is the clearest real-world impact
     number. With defaults, an unpatched build should retain about `120 MB` of
     view-model payload.

3. `Run preloaded VSM replacement`
   - Each page preloads the Northwind theme before the target view is attached.
   - Counts should return close to the control run. This demonstrates that the
     leak is tied to applying the swapped VSM groups after
     `OrientationStateTrigger.OnAttached` has subscribed to `DeviceDisplay`.

The app forces full GC before measurements so retained weak references are
meaningful. It also reports managed heap, GC heap, resident memory, and
working-set deltas after collection.

## Observed Result

Mac Catalyst autorun on 2026-06-25 used 40 pages/run, 3 MB payload/page, and
100 ms dwell/page.

Control with static orientation visual states:

- Retained pages: `0/40`
- Retained target visual elements: `0/40`
- Retained old `OrientationStateTrigger`s: `0/40`
- Retained payload view models: `0/40`

Live VSM replacement after attachment:

- Retained pages: `0/40`
- Retained target visual elements: `0/40`
- Retained old `OrientationStateTrigger`s: `40/40`
- Retained payload view models: `40/40`
- Retained payload: `120.0 MB`
- Managed heap delta after GC: `120.5 MB`
- GC heap delta after GC: `121.0 MB`

Control with preloaded VSM replacement before attachment:

- Retained pages: `0/40`
- Retained target visual elements: `0/40`
- Retained old `OrientationStateTrigger`s: `0/40`
- Retained payload view models: `0/40`

## Retention Chain

```text
DeviceDisplay.MainDisplayInfoChanged
  -> old OrientationStateTrigger
  -> explicit BindingContext / bound trigger state
  -> LeakPayloadViewModel
```
