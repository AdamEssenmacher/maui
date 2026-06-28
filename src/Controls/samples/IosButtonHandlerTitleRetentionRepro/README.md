# iOS ButtonHandler Title Retention Repro

This Mac Catalyst sample demonstrates that current iOS/Mac Catalyst `ButtonHandler` disconnect leaves large native `UIButton` title slots assigned when native peers survive through Objective-C reference counting.

The autorun scenario creates realistic workflow button labels, retains only native UIKit peers with Objective-C `retain`, disconnects the handler, and clears MAUI virtual-view state. The control path explicitly clears native title and attributed-title slots after disconnect.

Run:

```bash
dotnet run --project src/Controls/samples/IosButtonHandlerTitleRetentionRepro/IosButtonHandlerTitleRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
