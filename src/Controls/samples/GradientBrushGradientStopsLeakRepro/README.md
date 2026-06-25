# GradientBrush GradientStops leak repro

This sample demonstrates that assigning a long-lived `GradientStopCollection` to many short-lived `GradientBrush` instances leaves the brushes subscribed to the shared collection through strong `CollectionChanged` handlers.

`GradientBrush.UpdateGradientStops` subscribes to the current collection and to each `GradientStop.PropertyChanged`, but the framework has no detach path when the brush leaves the visual tree. A shared design-system `GradientStopCollection` can therefore retain closed-page brush instances indefinitely. The repro gives each brush explicit per-page binding state so the retained brushes also retain page-scoped payload objects.

The app runs three scenarios:

1. Control: every brush receives a fresh `GradientStopCollection`.
2. Leak: every brush receives the same shared `GradientStopCollection`.
3. Mitigation: every brush receives the shared collection, then replaces `GradientStops` on page disappearance.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/GradientBrushGradientStopsLeakRepro/GradientBrushGradientStopsLeakRepro.csproj -f net10.0-maccatalyst
GRADIENTBRUSH_GRADIENTSTOPS_LEAK_REPRO_AUTORUN=1 \
GRADIENTBRUSH_GRADIENTSTOPS_LEAK_REPRO_RESULTS=/private/tmp/gradientbrushgradientstopsleakrepro-results.txt \
artifacts/bin/GradientBrushGradientStopsLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/GradientBrushGradientStopsLeakRepro.app/Contents/MacOS/GradientBrushGradientStopsLeakRepro \
  --auto-run \
  --results=/private/tmp/gradientbrushgradientstopsleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.gradientbrushgradientstopsleakrepro/Data/Documents/GradientBrushGradientStopsLeakRepro/autorun-results.txt`

## Observed result

Mac Catalyst autorun on 2026-06-25 used 60 pages/run, 6 brushes/page, 12 stops/brush, and 2 MB payload/page.

Control with fresh collections:

- Retained pages: `0/60`
- Retained containers: `0/360`
- Retained `GradientBrush` instances: `0/360`
- Retained payload view models: `0/60`

Shared `GradientStopCollection`:

- Retained pages: `0/60`
- Retained containers: `0/360`
- Retained `GradientBrush` instances: `360/360`
- Retained payload view models: `60/60`
- Retained payload: `120.0 MB`
- Managed heap delta after GC: `122.5 MB`
- GC heap delta after GC: `124.9 MB`

Mitigation replacing `GradientStops` on page disappearance:

- Retained pages: `0/60`
- Retained containers: `0/360`
- Retained `GradientBrush` instances: `0/360`
- Retained payload view models: `0/60`
