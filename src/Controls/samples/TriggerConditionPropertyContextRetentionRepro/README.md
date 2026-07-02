# Trigger Condition Property Context Retention Repro

This repro shows that removing a `Trigger` or `DataTrigger` from a still-live target can leave the discarded trigger graph rooted through the target's private `BindableObject` property-context dictionary.

`PropertyCondition` and `BindingCondition` each create an instance-owned attached `BindableProperty` with a property-changed delegate targeting the condition instance. Their teardown paths call `ClearValue(...)`, but `BindableObject.ClearValueCore(...)` removes the value at the requested specificity and leaves the `BindablePropertyContext` in `_properties`. That stale context keeps the condition-owned `BindableProperty`, the condition, the removed trigger, and its `BindingContext` payload alive while the target remains alive.

The sample keeps 160 target `Label`s alive in both scenarios: 80 use `Trigger`/`PropertyCondition` and 80 use `DataTrigger`/`BindingCondition`. Each removed trigger carries a realistic 1 MiB payload in its `BindingContext`. The control scenario reflectively removes the stale condition-owned `BindablePropertyContext` from the retained target after trigger removal; current MAUI relies on condition teardown as-is.

Run:

```bash
dotnet build src/Controls/samples/TriggerConditionPropertyContextRetentionRepro/TriggerConditionPropertyContextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/TriggerConditionPropertyContextRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/TriggerConditionPropertyContextRetentionRepro.app
cat /tmp/trigger-condition-propertycontext-retention-results.txt
```

The autorun writes results to `/tmp/trigger-condition-propertycontext-retention-results.txt`.

Expected unpatched result:

```text
RESULT: PROVEN
...
Run: control: remove stale condition attached-property contexts after trigger removal
  removed Triggers alive after full GC: 0/160
  trigger payload buffers alive after full GC: 0/160
  retained payload bytes: 0.0 MiB

Run: current: remove Trigger/DataTrigger from retained target labels
  removed Triggers alive after full GC: 160/160
  trigger payload buffers alive after full GC: 160/160
  retained payload bytes: 160.0 MiB
```
