# DynamicResource Bindable Value Retention Repro

This sample proves that a live `Element` can retain old `BindableObject` values delivered through a `DynamicResource`.

The repro creates one live host element with a custom `BindableProperty` set by `SetDynamicResource`. It replaces the same resource key 128 times with realistic 1 MiB bindable payload objects, then replaces the final resource value with a non-bindable sentinel, removes the dynamic resource, clears the target property, and forces full GC.

Current MAUI stores each bindable resource value in the owner-side private `Element._bindableResources` list from `Element.OnResourcesChanged(...)`. That list is used to propagate binding context to bindable resources, but old entries are not removed when the resource key changes or when the dynamic resource is removed.

The control run keeps the same live host element but clears only `Element._bindableResources` after each update.

Expected result:

```text
RESULT: PROVEN
control: 0/128 payload resources and payload buffers retained
current: 128/128 payload resources and payload buffers retained
```

Run:

```bash
dotnet build src/Controls/samples/DynamicResourceBindableValueRetentionRepro/DynamicResourceBindableValueRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/DynamicResourceBindableValueRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/DynamicResourceBindableValueRetentionRepro.app
cat /tmp/dynamicresource-bindable-value-retention-results.txt
```
