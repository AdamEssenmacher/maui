# iOS ButtonRenderer Title Retention Repro

This Mac Catalyst sample demonstrates that legacy iOS compatibility `ButtonRenderer` disposal leaves native `UIButton` title slots assigned when native peers survive through Objective-C reference counting.

The autorun scenario creates 4,096 generated workflow button labels per run, retains only native UIKit peers with Objective-C `retain`, disposes the compatibility renderer, and clears MAUI virtual-view state. The control path explicitly clears native title and attributed-title slots before renderer disposal.

The harness uses a `ButtonRenderer` subclass only to suppress the legacy image-animation cleanup probe for `UIButton.ImageView`; leaving long titles assigned makes that unrelated cleanup path force UIKit label layout and throw before disposal completes. The title assignment and disposal behavior under test still comes from the base compatibility `ButtonRenderer`.

Expected proof output:

```text
Button renderer cycles per scenario: 4096
Payload per native button title: 1 KiB
Leak proved: True
Control native peers with assigned title: 0/4096
Current native peers with assigned title: 4096/4096
Current estimated retained native title MiB: 4.0
Alive renderers: 0/4096
Alive virtual views: 0/4096
```

Run:

```bash
dotnet run --project src/Controls/samples/IosButtonRendererTitleRetentionRepro/IosButtonRendererTitleRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
