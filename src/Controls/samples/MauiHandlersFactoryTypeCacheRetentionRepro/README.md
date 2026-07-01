# MauiHandlersFactoryTypeCacheRetentionRepro

This repro targets `Microsoft.Maui.Hosting.Internal.MauiHandlersFactory._serviceCache`.

`MauiHandlersFactory.GetHandlerType(Type)` resolves a concrete virtual-view type to a registered handler service type through `_serviceCache.GetOrAdd(...)`. The cache key is the concrete runtime view type. If an app host creates collectible or plugin-provided view types that are assignable to a registered base view type, the handler factory can retain each concrete type for the lifetime of the app even though the app never registered those concrete types.

The repro generates 80 collectible dynamic `BoxView` subclasses. Each dynamic type carries a 1 MiB static payload to model realistic plugin, tenant, form-builder, or dashboard modules that keep generated static metadata and cached data beside their view types. The MAUI handler collection registers only the base `BoxView` type. Each dynamic type is then resolved through the real `IMauiHandlersFactory.GetHandlerType(...)` path.

Two scenarios are compared:

1. `Control`: clears only `MauiHandlersFactory._serviceCache` before forced GC.
2. `Current MAUI`: leaves `_serviceCache` intact.

Run:

```bash
dotnet run --project src/Controls/samples/MauiHandlersFactoryTypeCacheRetentionRepro/MauiHandlersFactoryTypeCacheRetentionRepro.csproj -c Release -- --results=/tmp/mauihandlersfactory-type-cache-retention-results.txt
```

Expected failing/current result:

```text
Control: explicit _serviceCache.Clear()
  Retained types: 0/80
  Retained payloads: 0/80

Current MAUI: _serviceCache left intact
  Retained types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
```

The control demonstrates that the generated types and payloads collect when the handler-factory cache is cleared. The current run demonstrates that the live factory cache alone keeps the concrete types, their collectible assemblies, and their static payloads alive.
