# AdaptiveTriggerLeakRepro

This repro models a runtime tenant or brand theme refresh on visible responsive
dashboard controls. The theme refresh replaces `VisualStateManager.VisualStateGroups`
on an attached card so the dashboard can switch to a different set of responsive
states.

The leak is in the shared `AdaptiveTrigger` path where a trigger subscribes to
`Window.SizeChanged` and stores the target `VisualElement` strongly. If
`VisualStateGroups` is replaced while that element is already attached to a
`Window`, the old triggers are not detached. Later, when the element leaves the
window, MAUI only detaches triggers from the current VSM groups, so the old
`AdaptiveTrigger` can retain the old target view and its `BindingContext`.

Normal static XAML visual states are not expected to leak. The risky pattern is
runtime replacement of VSM groups on visible controls, such as tenant branding,
theme swapping, accessibility or density mode changes, or controls/templates
that rebuild responsive states while on screen.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/AdaptiveTriggerLeakRepro/AdaptiveTriggerLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/AdaptiveTriggerLeakRepro/AdaptiveTriggerLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/AdaptiveTriggerLeakRepro/AdaptiveTriggerLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/AdaptiveTriggerLeakRepro/AdaptiveTriggerLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst and write a result file:

```bash
ADAPTIVE_TRIGGER_LEAK_REPRO_AUTORUN=1 \
ADAPTIVE_TRIGGER_LEAK_REPRO_RESULTS=/private/tmp/adaptivetriggerleakrepro-results.txt \
dotnet run --project src/Controls/samples/AdaptiveTriggerLeakRepro/AdaptiveTriggerLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/adaptivetriggerleakrepro-results.txt
```

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/AdaptiveTriggerLeakRepro/AdaptiveTriggerLeakRepro.csproj -f net10.0-android -p:AdaptiveTriggerLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.adaptivetriggerleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

## What to Check

Use the default settings first:

- Pages/run: `40`
- Payload MB/page: `3`
- Dwell ms/page: `100`

Run these scenarios:

1. `Run control`
   - Each page sets `VisualStateGroups` with an `AdaptiveTrigger` once before
     the target view is attached.
   - After full GC, alive target visual elements and payload view models should
     stay near zero.

2. `Run live theme swap`
   - Each page starts with the Contoso responsive theme, waits until the target
     view is loaded and has a `Window`, then applies the Northwind theme.
   - On an unpatched build, the old `AdaptiveTrigger` remains subscribed to
     `Window.SizeChanged` and retains the target view.
   - `Payload retained by alive view models` is the clearest real-world impact
     number. With defaults, an unpatched build should retain about `120 MB` of
     view-model payload.

3. `Run preloaded theme swap`
   - Each page preloads the Northwind theme before the target view is attached.
   - Counts should return close to the control run. This demonstrates that the
     leak is tied to applying the swapped theme after `AdaptiveTrigger.OnAttached`
     has subscribed to the window.

The app forces full GC before measurements so retained weak references are
meaningful. It also reports managed heap, GC heap, resident memory, and
working-set deltas after collection.

## Retention Chain

```text
Window.SizeChanged
  -> old AdaptiveTrigger
  -> _visualElement
  -> target ContentView
  -> BindingContext / LeakPayloadViewModel
```
