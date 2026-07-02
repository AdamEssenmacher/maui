# Trigger Condition Property Context Retention Repro

This repro shows that removing a `Trigger`, `DataTrigger`, or `MultiTrigger` from a still-live target can leave the discarded trigger graph rooted through the target's private `BindableObject` property-context dictionary.

`PropertyCondition`, `BindingCondition`, and `MultiCondition` each create instance-owned attached `BindableProperty` state with a property-changed delegate targeting the condition instance. Their teardown paths either call `ClearValue(...)` or skip clearing the aggregate property, and `BindableObject.ClearValueCore(...)` removes values without removing the `BindablePropertyContext` from `_properties`. That stale context keeps the condition-owned `BindableProperty`, the condition, the removed trigger, and its `BindingContext` payload alive while the target remains alive.

The sample keeps 180 target `Label`s alive in both scenarios: 60 use `Trigger`/`PropertyCondition`, 60 use `DataTrigger`/`BindingCondition`, and 60 use `MultiTrigger`/`MultiCondition`. Each removed trigger carries a realistic 1 MiB payload in its `BindingContext`. The control scenario reflectively removes stale condition-owned `BindablePropertyContext` entries from the retained target after trigger removal; current MAUI relies on condition teardown as-is.

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
  removed Triggers alive after full GC: 0/180
  trigger payload buffers alive after full GC: 0/180
  retained payload bytes: 0.0 MiB

Run: current: remove Trigger/DataTrigger/MultiTrigger from retained target labels
  removed Triggers alive after full GC: 180/180
  trigger payload buffers alive after full GC: 180/180
  retained payload bytes: 180.0 MiB
```
