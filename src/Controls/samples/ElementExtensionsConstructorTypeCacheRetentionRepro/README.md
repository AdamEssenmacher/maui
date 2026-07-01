# ElementExtensionsConstructorTypeCacheRetentionRepro

This repro targets `Microsoft.Maui.Platform.ElementExtensions.handlersWithConstructors`.

`ElementExtensions.ToHandler(...)` first tries `IMauiHandlersFactory.GetHandler(viewType)`. If the registered handler type has no public parameterless constructor, the factory throws `MissingMethodException`; `ToHandler(...)` then creates the handler through `ActivatorUtilities.CreateInstance(...)` and stores the concrete runtime view type in the static `handlersWithConstructors` set so later calls skip the failing default factory path.

The cache key is the concrete runtime view type. If an app host creates collectible, plugin-provided, tenant-specific, or generated view types that are assignable to a registered base view type, and the registered handler requires constructor injection, the static fallback cache can retain every generated concrete view type for the lifetime of the process.

The repro generates 80 collectible dynamic `BoxView` subclasses. Each dynamic type carries a 1 MiB static payload to model realistic plugin, tenant, form-builder, or dashboard modules that keep generated static metadata and cached data beside their view types. The MAUI handler collection registers only the base `BoxView` type with an injected-constructor handler. Each dynamic type is then resolved through the real `ElementExtensions.ToHandler(...)` path.

To isolate this leak from `MauiHandlersFactory._serviceCache`, both scenarios clear the handler-factory cache before forced GC.

Two scenarios are compared:

1. `Control`: clears `ElementExtensions.handlersWithConstructors` and `MauiHandlersFactory._serviceCache` before forced GC.
2. `Current MAUI`: clears `MauiHandlersFactory._serviceCache` but leaves `handlersWithConstructors` intact.

Run:

```bash
dotnet run --project src/Controls/samples/ElementExtensionsConstructorTypeCacheRetentionRepro/ElementExtensionsConstructorTypeCacheRetentionRepro.csproj -c Release -- --results=/tmp/elementextensions-constructor-type-cache-retention-results.txt
```

Expected failing/current result:

```text
Control: explicit handlersWithConstructors.Clear()
  Constructor-cache entries: 0
  Factory cache entries: 0
  Retained types: 0/80
  Retained payloads: 0/80

Current MAUI: handlersWithConstructors left intact
  Constructor-cache entries: 80
  Factory cache entries: 0
  Retained types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
```

The control demonstrates that the generated types and payloads collect when the constructor-fallback cache is cleared. The current run demonstrates that the static fallback cache alone keeps the concrete types, their collectible assemblies, and their static payloads alive.

This is distinct from the handler-factory service-cache leak: that cache lives on the app-lifetime `MauiHandlersFactory`, while this repro clears the factory cache in both scenarios and still proves retention through the process-static constructor-fallback set.
