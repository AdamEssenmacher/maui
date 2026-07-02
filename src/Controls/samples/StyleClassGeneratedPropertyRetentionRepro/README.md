# StyleClass Generated Property Retention Repro

This sample proves that style-class churn can retain removed style resources through stale generated `ClassStyle` bindable-property contexts on a live target.

Run it for Mac Catalyst:

```bash
dotnet build src/Controls/samples/StyleClassGeneratedPropertyRetentionRepro/StyleClassGeneratedPropertyRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/StyleClassGeneratedPropertyRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/StyleClassGeneratedPropertyRetentionRepro.app
cat /tmp/styleclass-generated-property-retention-results.txt
```

Expected result: `RESULT: PROVEN`.

The repro keeps one `Label` alive in both scenarios, churns 120 unique `StyleClass` values, and removes the corresponding resource dictionary entries after each class is applied. Each generated class style contains a setter payload with a 1 MiB buffer. The control path reflectively removes stale generated `ClassStyle` property contexts while preserving the current final empty class context.
