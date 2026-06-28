# iOS Compatibility PickerRenderer Text Retention Repro

This Mac Catalyst sample demonstrates that legacy iOS compatibility `PickerRenderer` disposal leaves large native `UITextField` text slots assigned when native peers survive through Objective-C reference counting.

The autorun scenario creates realistic 512 KiB picker item payloads, retains only native UIKit peers with Objective-C `retain`, disposes the compatibility renderer, and clears MAUI virtual-view items. The control path explicitly clears native `Text`, `AttributedText`, placeholder, and input accessory slots before disposal.

Run:

```bash
dotnet run --project src/Controls/samples/IosCompatPickerRendererTextRetentionRepro/IosCompatPickerRendererTextRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
