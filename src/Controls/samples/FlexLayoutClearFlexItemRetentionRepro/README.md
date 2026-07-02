# FlexLayout Clear FlexItem Retention Repro

This sample proves that `FlexLayout.Clear()` can leave stale private `FlexItem` attached property values on removed children. If one removed child is kept alive by app code, a temporary platform root, or a view reuse/cache path, that child can keep the old flex root alive. The old root still contains the sibling flex items, and each sibling item has a `SelfSizing` delegate that captures its child view.

The repro compares three scenarios:

- Baseline: `Clear()` with no retained removed child.
- Control: one removed child is retained, but children are removed one by one with `RemoveAt`, which runs `RemoveFlexItem`.
- Current: one removed child is retained and `FlexLayout.Clear()` is used.

The current scenario retains 40 sentinel children intentionally and then shows whether the 120 sibling payloads also survive. Each payload has a 1 MiB buffer, so retaining the siblings demonstrates a 120 MiB stale graph.

Build and run:

```bash
dotnet build src/Controls/samples/FlexLayoutClearFlexItemRetentionRepro/FlexLayoutClearFlexItemRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W "artifacts/bin/FlexLayoutClearFlexItemRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/FlexLayout Clear FlexItem Retention.app" --args --results=/tmp/flexlayout-clear-flexitem-retention-results.txt
```
