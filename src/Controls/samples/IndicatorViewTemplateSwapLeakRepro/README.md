# IndicatorViewTemplateSwapLeakRepro

This standalone MAUI sample targets the shared `IndicatorView` template replacement path in:

- `src/Controls/src/Core/IndicatorView/IndicatorView.cs`
- `src/Controls/src/Core/IndicatorView/IndicatorStackLayout.cs`

When `IndicatorTemplate` changes from one non-null `DataTemplate` to another, `IndicatorView` installs a new internal `IndicatorStackLayout` but does not call `Remove()` on the previous one. The old layout stays subscribed to `IndicatorView.PropertyChanged`, which keeps the retired layout and its templated child views alive for as long as the `IndicatorView` stays alive.

This is not a page-lifetime repro. The page stays alive on purpose and repeatedly replaces `IndicatorTemplate` on the same `IndicatorView`.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/IndicatorViewTemplateSwapLeakRepro/IndicatorViewTemplateSwapLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/IndicatorViewTemplateSwapLeakRepro/IndicatorViewTemplateSwapLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/IndicatorViewTemplateSwapLeakRepro/IndicatorViewTemplateSwapLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/IndicatorViewTemplateSwapLeakRepro/IndicatorViewTemplateSwapLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst:

```bash
INDICATOR_TEMPLATE_SWAP_LEAK_REPRO_AUTORUN=1 \
dotnet run --project src/Controls/samples/IndicatorViewTemplateSwapLeakRepro/IndicatorViewTemplateSwapLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run
```

The app prints the resolved results-file path before it exits.

If you want to request a specific results path, pass `--results=...` or `INDICATOR_TEMPLATE_SWAP_LEAK_REPRO_RESULTS=...`. That path still has to be writable by the app process. If it is not, the sample logs the failure and prints the fallback path that succeeded.

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/IndicatorViewTemplateSwapLeakRepro/IndicatorViewTemplateSwapLeakRepro.csproj -f net10.0-android -p:IndicatorViewTemplateSwapLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.indicatorviewtemplateswapleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

## What to Check

Use the default settings first:

- Template states/run: `40`
- Indicator items: `8`
- Payload KB/indicator: `192`
- Post-GC position updates: `1000`

Run these scenarios:

1. `Run control`
   - Creates one initial non-null `IndicatorTemplate` and keeps it for the entire run.
   - The configured default is still `40` template states, but the control intentionally realizes only the initial state.
   - Because no layout is retired, `retired layouts` and `retained retired payload` should stay at zero.

2. `Run leak`
   - Alternates directly between two different non-null templates on the same live `IndicatorView`.
   - With the defaults, this realizes `40` template states total: `1` initial state plus `39` direct non-null replacements.
   - On an unpatched build, the previous `IndicatorStackLayout` generations stay alive after full GC.
   - With the defaults, the expected retained retired payload is about `58.5 MB`: `39 retired layouts * 8 indicator children * 192 KB`.
   - `post-run slowdown` is the same-host impact metric: every leaked retired layout still handles `PositionProperty` changes.

3. `Run mitigation`
   - Clears `IndicatorTemplate` to `null`, waits for the layout to clear, then assigns the next non-null template.
   - With the defaults, this also realizes `40` template states total.
   - Counts should return close to the control run. This demonstrates that the missing old-layout detach is the retention root.

The app forces full GC before measurements so the weak-reference counts are meaningful. It also reports managed heap, GC heap, resident memory, and working-set deltas after collection.

## Observed Mac Catalyst Run

On an unpatched local `maccatalyst-arm64` build, the default autorun produced:

```text
Run: control: keep one non-null template
Template states configured: 40
Template states realized: 1
Retired layouts tracked: 0
Indicator items per layout: 8
Weak refs still alive after full GC:
  retired layouts: 0/0
  retired payload behaviors: 0/0
Retained retired payload: 0 B (0.0% of retired payload budget)
Position update burst after full GC:
  baseline: 175.4 ms for 1000 updates
  post-run: 137.8 ms for 1000 updates
  slowdown: 0.8x
Managed heap delta after GC: 9.9 KB
GC heap delta after GC: 0 B
Resident memory delta: 5.6 MB
Working set delta: 5.5 MB
Elapsed: 00:00

Run: leak: direct non-null template replacement
Template states configured: 40
Template states realized: 40
Retired layouts tracked: 39
Indicator items per layout: 8
Weak refs still alive after full GC:
  retired layouts: 39/39
  retired payload behaviors: 312/312
Retained retired payload: 58.5 MB (100.0% of retired payload budget)
Position update burst after full GC:
  baseline: 133.1 ms for 1000 updates
  post-run: 592.7 ms for 1000 updates
  slowdown: 4.5x
Managed heap delta after GC: 72.6 MB
GC heap delta after GC: 79.2 MB
Resident memory delta: 59.7 MB
Working set delta: 59.7 MB
Elapsed: 00:05

Run: mitigation: clear template before replacement
Template states configured: 40
Template states realized: 40
Retired layouts tracked: 39
Indicator items per layout: 8
Weak refs still alive after full GC:
  retired layouts: 0/39
  retired payload behaviors: 0/312
Retained retired payload: 0 B (0.0% of retired payload budget)
Position update burst after full GC:
  baseline: 136.2 ms for 1000 updates
  post-run: 136.3 ms for 1000 updates
  slowdown: 1.0x
Managed heap delta after GC: -2.7 KB
GC heap delta after GC: 3.1 MB
Resident memory delta: 2.9 MB
Working set delta: 2.9 MB
Elapsed: 00:00

Autorun results written to: /Users/adam/Library/Containers/com.microsoft.maui.indicatorviewtemplateswapleakrepro/Data/Documents/IndicatorViewTemplateSwapLeakRepro/autorun-results.txt
```
