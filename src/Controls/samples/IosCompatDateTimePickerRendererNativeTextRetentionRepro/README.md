# iOS Compatibility DatePickerRenderer/TimePickerRenderer Native Text Retention Repro

This Mac Catalyst sample demonstrates that legacy iOS compatibility `DatePickerRenderer` and `TimePickerRenderer` disposal leaves formatted native `UITextField` text slots assigned when native peers survive through Objective-C reference counting.

The autorun scenario creates realistic 8 KiB custom date/time display format payloads, retains only native UIKit text-field peers with Objective-C `retain`, disposes the compatibility renderers, and clears MAUI virtual-view formats. The control path explicitly clears native `Text`, `AttributedText`, and input-view/accessory slots before renderer disposal.

Run:

```bash
dotnet run --project src/Controls/samples/IosCompatDateTimePickerRendererNativeTextRetentionRepro/IosCompatDateTimePickerRendererNativeTextRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
