# DisplayRotationStateTriggerLeakRepro

This repro models a runtime tenant or brand theme refresh on visible
rotation-aware dashboard controls. The refresh replaces
`VisualStateManager.VisualStateGroups` on an attached card so the dashboard can
switch to a different set of visual states.

The leak is in the shared state-trigger replacement path.
`DisplayRotationStateTrigger` subscribes to
`DeviceDisplay.MainDisplayInfoChanged` in `OnAttached()` and unsubscribes in
`OnDetached()`. If `VisualStateGroups` is replaced while the element is already
attached to a `Window`, the old triggers are not detached. Later, when the
element leaves the window, MAUI only detaches triggers from the current VSM
groups, so the old `DisplayRotationStateTrigger` remains rooted by
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
dotnet build src/Controls/samples/DisplayRotationStateTriggerLeakRepro/DisplayRotationStateTriggerLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/DisplayRotationStateTriggerLeakRepro/DisplayRotationStateTriggerLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/DisplayRotationStateTriggerLeakRepro/DisplayRotationStateTriggerLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/DisplayRotationStateTriggerLeakRepro/DisplayRotationStateTriggerLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst and write a result file:

```bash
DISPLAY_ROTATION_STATE_TRIGGER_LEAK_REPRO_AUTORUN=1 \
DISPLAY_ROTATION_STATE_TRIGGER_LEAK_REPRO_RESULTS=/private/tmp/displayrotationstatetriggerleakrepro-results.txt \
dotnet run --project src/Controls/samples/DisplayRotationStateTriggerLeakRepro/DisplayRotationStateTriggerLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/displayrotationstatetriggerleakrepro-results.txt
```

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/DisplayRotationStateTriggerLeakRepro/DisplayRotationStateTriggerLeakRepro.csproj -f net10.0-android -p:DisplayRotationStateTriggerLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.displayrotationstatetriggerleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

## What to Check

Use the default settings first:

- Pages/run: `40`
- Payload MB/page: `3`
- Dwell ms/page: `100`

Run these scenarios:

1. `Run control`
   - Each page sets `VisualStateGroups` with a `DisplayRotationStateTrigger`
     once before the target view is attached.
   - After full GC, old triggers and payload view models should stay near zero.

2. `Run live VSM replacement`
   - Each page starts with the Contoso rotation theme, waits until the target
     view is loaded and has a `Window`, then applies the Northwind theme by
     replacing `VisualStateGroups`.
   - On an unpatched build, the old `DisplayRotationStateTrigger` remains
     subscribed to `DeviceDisplay.MainDisplayInfoChanged` and retains the
     trigger-local payload view model.
   - `Payload retained by alive view models` is the clearest real-world impact
     number. With defaults, an unpatched build should retain about `120 MB` of
     view-model payload.

3. `Run preloaded VSM replacement`
   - Each page preloads the Northwind theme before the target view is attached.
   - Counts should return close to the control run. This demonstrates that the
     leak is tied to applying the swapped VSM groups after
     `DisplayRotationStateTrigger.OnAttached` has subscribed to `DeviceDisplay`.

The app forces full GC before measurements so retained weak references are
meaningful. It also reports managed heap, GC heap, resident memory, and
working-set deltas after collection.

## Retention Chain

```text
DeviceDisplay.MainDisplayInfoChanged
  -> old DisplayRotationStateTrigger
  -> explicit BindingContext / bound trigger state
  -> LeakPayloadViewModel
```
