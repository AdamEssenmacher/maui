# BindableLayout Template Marker Retention Repro

This sample proves that `BindableLayout` generated children keep a hidden `BindableLayoutTemplate` attached property after removal. The removal paths clear generated item `BindingContext` values, but not the template marker. If one removed generated child is retained by app code, a view cache, or temporary platform cleanup, that child can keep a page-local runtime-XAML `DataTemplate` alive. The template factory then keeps the discarded XAML root page and its payload alive.

The repro compares three scenarios:

- Baseline: generated children are removed and none are retained.
- Control: one removed generated child is retained, but the hidden template marker is cleared.
- Current: one removed generated child is retained with the hidden template marker still attached.

The current scenario retains 80 discarded pages with 1 MiB page payloads while item model binding contexts are cleared, proving the root is the template marker rather than the removed item data.

Build and run:

```bash
dotnet build src/Controls/samples/BindableLayoutTemplateMarkerRetentionRepro/BindableLayoutTemplateMarkerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W "artifacts/bin/BindableLayoutTemplateMarkerRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/BindableLayout Template Marker Retention.app" --args --results=/tmp/bindablelayout-template-marker-retention-results.txt
```
