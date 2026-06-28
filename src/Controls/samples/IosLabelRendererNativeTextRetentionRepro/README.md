# iOS LabelRenderer Native Text Retention Repro

This Mac Catalyst sample demonstrates that legacy iOS `LabelRenderer` disposal leaves large native `UILabel.Text` / `AttributedText` slots assigned when the native label peer survives through Objective-C reference counting.

The sample intentionally uses plain `Label.Text`, not `FormattedText`, to avoid re-proving the separate `LabelRenderer._formatted` managed-field leak.

Run:

```bash
dotnet run --project src/Controls/samples/IosLabelRendererNativeTextRetentionRepro/IosLabelRendererNativeTextRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
