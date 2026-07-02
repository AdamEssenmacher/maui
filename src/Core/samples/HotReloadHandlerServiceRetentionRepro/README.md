# Hot Reload HandlerService Retention Repro

This repro proves that the current `MauiHotReloadHelper.HandlerService` static field can retain the last disposed app's handler collection and dynamic handler registration types.

`HandlerMauiAppBuilderExtensions.HandlerServiceBuilder` always calls `MauiHotReloadHelper.RegisterHandlers(this)` from its constructor. `RegisterHandlers` assigns the collection to a process-static strong field even when Hot Reload is not enabled. If a host builds and disposes a throwaway `MauiApp`, that static last-value field can keep the disposed app's `IMauiHandlersCollection` and its service descriptors alive.

The repro creates 80 collectible dynamic assemblies during the real `ConfigureMauiHandlers(...)` registration callback. Each assembly defines one virtual-view type implementing `IElement`, one handler type implementing `IElementHandler`, a 1 MiB static payload on the view type, and a 1 MiB static payload on the handler type. It resolves the app's handler collection, disposes the app, clears `RegisteredHandlerServiceTypeSet.s_instances` in both scenarios to isolate this from C024, and then compares clearing versus leaving only `MauiHotReloadHelper.HandlerService`.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/HotReloadHandlerServiceRetentionRepro/HotReloadHandlerServiceRetentionRepro.csproj -c Release -- --results=/tmp/hotreload-handlerservice-retention-results.txt
```

Latest local result:

```text
MAUI Hot Reload handler-service retention repro
Result: PROVEN

Trigger:
  HandlerServiceBuilder always calls MauiHotReloadHelper.RegisterHandlers(this), even when Hot Reload is not enabled.
  RegisterHandlers stores the app's IMauiHandlersCollection in the process-static HandlerService field.
  After a throwaway MauiApp is disposed, that static last-value field can keep the disposed app's handler collection and registration descriptors alive.
  This repro clears RegisteredHandlerServiceTypeSet.s_instances in both scenarios to isolate this from C024.

Dynamic handler registrations: 80
Payload per dynamic view type: 1 MiB
Payload per dynamic handler type: 1 MiB

Control: MauiHotReloadHelper.HandlerService cleared after app disposal and before forced GC
  HotReload HandlerService descriptors before collect: 80
  HotReload HandlerService descriptors after collect: 0
  RegisteredHandlerServiceTypeSet instances before collect: 1
  RegisteredHandlerServiceTypeSet instances after collect: 0
  Retained assemblies: 0
  Retained view types: 0
  Retained handler types: 0
  Retained view payloads: 0
  Retained handler payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 61,616 bytes

Current MAUI: MauiHotReloadHelper.HandlerService left intact after app disposal
  HotReload HandlerService descriptors before collect: 80
  HotReload HandlerService descriptors after collect: 80
  RegisteredHandlerServiceTypeSet instances before collect: 1
  RegisteredHandlerServiceTypeSet instances after collect: 0
  Retained assemblies: 80
  Retained view types: 80
  Retained handler types: 80
  Retained view payloads: 80
  Retained handler payloads: 80
  Retained payload bytes: 167,772,160
  Managed heap delta: 167,838,672 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/HotReloadHandlerServiceRetentionRepro/HotReloadHandlerServiceRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `MauiHotReloadHelper HandlerService memory leak`, `HotReloadHelper memory leak`, `RegisterHandlers HotReload retention`, and `HandlerService Hot Reload leak` found no exact tracking issue. Fork branch filters for `hotreload handler`, `hot-reload handler`, `hotreload`, `hot-reload`, `handler-service`, `handlerservice`, and `last handler` found only upstream feature work, not a repro branch for this static last-value root.
