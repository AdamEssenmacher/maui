# Style BaseResourceKey Retention Repro

This sample proves that applying and removing dynamic styles with unique `BaseResourceKey` values can retain removed base style resources through stale hidden `BasedOnResource` bindable-property contexts on a live target.

Run it for Mac Catalyst:

```bash
dotnet build src/Controls/samples/StyleBaseResourceKeyRetentionRepro/StyleBaseResourceKeyRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/StyleBaseResourceKeyRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/StyleBaseResourceKeyRetentionRepro.app
cat /tmp/style-baseresourcekey-retention-results.txt
```

Expected result: `RESULT: PROVEN`.

The repro keeps one `Label` alive in both scenarios, applies and removes 120 unique derived styles, and removes each matching base style resource after the derived style is unapplied. Each base style contains a setter payload with a 1 MiB buffer. The control path reflectively removes stale hidden `BasedOnResource` property contexts from the retained target after each unapply.
