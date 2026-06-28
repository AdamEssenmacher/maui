# iOS Compatibility Text Renderer Retention Repro

This Mac Catalyst sample demonstrates that legacy iOS `EntryRenderer` and `EditorRenderer` disposal leaves large native `UITextField` and `UITextView` text slots assigned when the native peers survive through Objective-C reference counting.

The autorun scenario creates realistic 512 KiB document/search text payloads, retains only native UIKit peers with Objective-C `retain`, disposes the compatibility renderers, and clears MAUI virtual-view text. The control path explicitly clears native `Text`, `AttributedText`, and placeholder text slots before disposal.

Run:

```bash
dotnet run --project src/Controls/samples/IosCompatTextRendererRetentionRepro/IosCompatTextRendererRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
