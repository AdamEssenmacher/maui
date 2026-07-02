# LifecycleEventService Delegate Retention Repro

This repro proves that the current `Microsoft.Maui.LifecycleEvents.LifecycleEventService` registration map can retain collectible plugin/module assemblies through app-lifetime lifecycle-event delegate metadata.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a static lifecycle callback method and a 1 MiB static payload to model a small plugin/module lifecycle hook. It then feeds those callbacks through the same `LifecycleEventRegistration` / `LifecycleEventService` path used by `ConfigureLifecycleEvents(...)`.

`LifecycleEventService` stores each registered callback delegate in its private `_mapper` dictionary. In the control scenario, the repro clears that dictionary before forcing GC. In current MAUI, the dictionary is left intact.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/LifecycleEventServiceDelegateRetentionRepro/LifecycleEventServiceDelegateRetentionRepro.csproj -c Release -- --results=/tmp/lifecycleeventservice-delegate-retention-results.txt
```

Latest local result:

```text
LifecycleEventService delegate retention repro
Result: PROVEN

Trigger:
  ConfigureLifecycleEvents creates app-lifetime LifecycleEventRegistration entries.
  LifecycleEventService copies registered delegates into its private _mapper dictionary.
  ILifecycleEventService exposes read/invoke state but no public remove or scoped registration API.
  Plugin/module lifecycle delegates can therefore stay rooted after the plugin should unload.

Dynamic lifecycle delegates: 80
Payload per dynamic delegate type: 1 MiB

Control: LifecycleEventService._mapper cleared before forced GC
  Event names before collect: 1
  Event names after collect: 0
  Lifecycle delegates before collect: 80
  Lifecycle delegates after collect: 0
  Retained assemblies: 0
  Retained delegate types: 0
  Retained payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 40,344 bytes

Current MAUI: LifecycleEventService._mapper left intact
  Event names before collect: 1
  Event names after collect: 1
  Lifecycle delegates before collect: 80
  Lifecycle delegates after collect: 80
  Retained assemblies: 80
  Retained delegate types: 80
  Retained payloads: 80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,933,008 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/LifecycleEventServiceDelegateRetentionRepro/LifecycleEventServiceDelegateRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `ConfigureLifecycleEvents`, `LifecycleEventService`, `LifecycleEventRegistration`, and lifecycle-event memory/retention wording found no exact tracking issue for lifecycle delegate metadata retaining unloadable plugin/module assemblies. Fork branch filters for `lifecycleevent`, `lifecycle-event`, `configurelifecycle`, `lifecycle-delegate`, `eventservice`, and `app-lifecycle` found no existing repro branch.
