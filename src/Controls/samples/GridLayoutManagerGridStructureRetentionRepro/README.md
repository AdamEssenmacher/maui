# GridLayoutManager GridStructure Retention Repro

This repro demonstrates that a live `Grid` can retain removed children after it has been measured and then cleared.

`GridLayoutManager.Measure()` stores the last `GridStructure` in `_gridStructure`. The nested structure stores the measured children in `_childrenToLayOut`. `Grid.Clear()` removes public children and logical children, but it does not clear the cached `GridStructure`, so a live emptied `Grid` can keep removed child views and their binding payloads alive until another measure replaces the structure.

Run:

```bash
dotnet build src/Controls/samples/GridLayoutManagerGridStructureRetentionRepro/GridLayoutManagerGridStructureRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W "artifacts/bin/GridLayoutManagerGridStructureRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/GridLayoutManager GridStructure Retention.app" --args --results=/tmp/gridlayoutmanager-gridstructure-retention-results.txt
cat /tmp/gridlayoutmanager-gridstructure-retention-results.txt
```
