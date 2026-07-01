# RadioButtonGroup Stale Controller Retention Repro

This repro demonstrates that a `RadioButton` whose `GroupName` is changed away from an attached `RadioButtonGroup` can remain mapped to the old `RadioButtonGroupController`.

The sample retains only the changed-away `RadioButton` handles. In the current MAUI path, those retained radio buttons keep the old group layouts and unrelated sibling payloads alive through the static `ConditionalWeakTable<RadioButton, RadioButtonGroupController>`. The control path explicitly removes that stale weak-table entry after the group-name change.

Run with:

```bash
dotnet build src/Controls/samples/RadioButtonGroupStaleControllerRetentionRepro/RadioButtonGroupStaleControllerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W artifacts/bin/RadioButtonGroupStaleControllerRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/RadioButtonGroupStaleControllerRetentionRepro.app
cat /tmp/radiobuttongroup-stale-controller-retention-results.txt
```
