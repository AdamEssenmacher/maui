# MAUI Handlers Registration Type Retention Repro

This repro proves that the current `ConfigureMauiHandlers(...)` / `IMauiHandlersCollection.AddHandler(Type, Type)` registration path can retain collectible plugin/module view and handler types for the app lifetime.

The repro creates 80 collectible dynamic assemblies during the real `ConfigureMauiHandlers(...)` registration callback. Each assembly defines one virtual-view type implementing `IElement`, one handler type implementing `IElementHandler`, a 1 MiB static payload on the view type, and a 1 MiB static payload on the handler type. It registers each pair through the public `AddHandler(Type, Type)` API and then keeps the app/service provider alive.

`AddHandler(Type, Type)` stores each virtual-view type in `RegisteredHandlerServiceTypeSet` and adds a transient service descriptor that stores the view/handler type pair in the app-lifetime `MauiServiceCollection`. In the control scenario, the repro clears only the handler collection descriptors and registered type sets before forcing GC. In current MAUI, that registration state is left intact.

This is distinct from the C024 hosting service-collection repro: C024 proves a process-static dictionary retains otherwise-dead throwaway handler collections. This repro keeps the app-lifetime handler collection alive in both scenarios and isolates the dynamic registration metadata inside the live collection.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/MauiHandlersRegistrationTypeRetentionRepro/MauiHandlersRegistrationTypeRetentionRepro.csproj -c Release -- --results=/tmp/mauihandlers-registration-type-retention-results.txt
```

Latest local result:

```text
MAUI handler registration type-retention repro
Result: PROVEN

Trigger:
  ConfigureMauiHandlers(...) feeds public IMauiHandlersCollection.AddHandler(Type, Type) registrations into the app-lifetime handler collection.
  AddHandler stores each virtual-view Type in RegisteredHandlerServiceTypeSet and each view/handler Type pair in MauiServiceCollection service descriptors.
  There is no public unregister or scoped eviction path for dynamically loaded handler registrations while the app-lifetime provider lives.
  Plugin/module handler registrations can therefore stay rooted after the plugin should unload.

Dynamic handler registrations: 80
Payload per dynamic view type: 1 MiB
Payload per dynamic handler type: 1 MiB

Control: IMauiHandlersCollection descriptors and registered type sets cleared before forced GC
  Service descriptors before collect: 80
  Service descriptors after collect: 0
  Concrete registered view types before collect: 80
  Concrete registered view types after collect: 0
  Interface registered view types before collect: 0
  Interface registered view types after collect: 0
  Retained assemblies: 0
  Retained view types: 0
  Retained handler types: 0
  Retained view payloads: 0
  Retained handler payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 123,200 bytes

Current MAUI: IMauiHandlersCollection registration state left intact
  Service descriptors before collect: 80
  Service descriptors after collect: 80
  Concrete registered view types before collect: 80
  Concrete registered view types after collect: 80
  Interface registered view types before collect: 0
  Interface registered view types after collect: 0
  Retained assemblies: 80
  Retained view types: 80
  Retained handler types: 80
  Retained view payloads: 80
  Retained handler payloads: 80
  Retained payload bytes: 167,772,160
  Managed heap delta: 167,898,272 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/MauiHandlersRegistrationTypeRetentionRepro/MauiHandlersRegistrationTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `ConfigureMauiHandlers memory leak`, `IMauiHandlersCollection retention`, `HandlerServiceBuilder`, `HandlerRegistration`, `RegisteredHandlerServiceTypeSet`, and `AddHandler memory leak` found no exact tracking issue for app-lifetime handler registration metadata retaining collectible view/handler types. Fork branch filters for `handler registration`, `mauihandler`, `handler collection`, `handler type`, `service-collection`, `hosting`, `registered handler`, and `addhandler` found only adjacent `origin/repro/hosting-service-collection-cache-leak-20260626` and `origin/repro/mauihandlersfactory-type-cache-retention-20260701`, not this live registration-state class.
