# RelativeBindingSource TemplatedParent Pending Retention Repro

This sample tests whether removed `Binding` instances that use `RelativeBindingSource.TemplatedParent` can stay alive through pending templated-parent lookup tasks on a retained off-tree target element.

Run it for Mac Catalyst:

```bash
dotnet build src/Controls/samples/RelativeBindingTemplatedParentPendingRetentionRepro/RelativeBindingTemplatedParentPendingRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/RelativeBindingTemplatedParentPendingRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/RelativeBindingTemplatedParentPendingRetentionRepro.app
cat /tmp/relativebinding-templatedparent-pending-retention-results.txt
```

Expected result: `RESULT: PROVEN`.

The repro keeps one target `Label` alive in both scenarios and applies then removes 120 `Binding` instances whose converters each carry a 1 MiB payload. The control parents the label under a templated host before applying the bindings, so `FindTemplatedParentAsync` completes. The current path keeps the label off-tree, so each removed relative-source binding leaves a pending `ParentSet` handler whose async continuation still captures the old binding and converter payload.
