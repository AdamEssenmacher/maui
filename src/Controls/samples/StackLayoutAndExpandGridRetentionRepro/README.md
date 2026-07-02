# StackLayout AndExpand Grid Retention Repro

This sample proves that `StackLayout` can retain removed children after a prior `FillAndExpand` layout pass.

`StackLayoutManager` selects `AndExpandLayoutManager` when a child uses the orientation-matching `LayoutOptions.*AndExpand` option. `AndExpandLayoutManager.Measure()` builds a private `Grid` mirror of the stack children and stores it in `_gridLayout`. Clearing the public `StackLayout.Children` later does not clear that private grid mirror, so a live `StackLayout` can keep removed child views and their binding payloads alive.

The repro compares three scenarios:

- Baseline: live `StackLayout`s are cleared without a prior AndExpand measure.
- Control: live `StackLayout`s are measured first, then cleared, and the stale private AndExpand manager is explicitly cleared.
- Current: live `StackLayout`s are measured first and then cleared with current MAUI behavior.

The current scenario keeps 60 empty `StackLayout`s alive and shows whether their 180 removed children remain alive. Each child has a 1 MiB payload, so the stale grid mirror demonstrates 180 MiB of retained payload.

Build and run:

```bash
dotnet build src/Controls/samples/StackLayoutAndExpandGridRetentionRepro/StackLayoutAndExpandGridRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W "artifacts/bin/StackLayoutAndExpandGridRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/StackLayout AndExpand Grid Retention.app" --args --results=/tmp/stacklayout-andexpand-grid-retention-results.txt
```
