# ImageSourceServiceProviderTypeCacheRetentionRepro

This repro targets `Microsoft.Maui.Hosting.ImageSourceServiceProvider._serviceCache`.

`ImageSourceServiceProvider.GetImageSourceService(Type)` resolves a concrete image-source type to a registered image-source service type through `_serviceCache.GetOrAdd(...)`. The cache key is the concrete runtime image-source type. If an app host creates collectible, plugin-provided, tenant-specific, or generated image-source descriptor types that are assignable to a registered base image-source type, the provider can retain each concrete type for the lifetime of the `MauiApp` even though the app never registered those concrete types.

The repro generates 80 collectible dynamic `ImageSource` subclasses. Each dynamic type carries a 1 MiB static payload to model realistic plugin, tenant, form-builder, media-catalog, or dashboard modules that keep generated static metadata and cached image descriptors beside their image-source types. The app registers only the base `ImageSource` type with a no-op image-source service. Each dynamic type is then resolved through the real `IImageSourceServiceProvider.GetImageSourceService(...)` path.

Two scenarios are compared:

1. `Control`: clears only `ImageSourceServiceProvider._serviceCache` before forced GC.
2. `Current MAUI`: leaves `_serviceCache` intact.

Run:

```bash
dotnet run --project src/Controls/samples/ImageSourceServiceProviderTypeCacheRetentionRepro/ImageSourceServiceProviderTypeCacheRetentionRepro.csproj -c Release -- --results=/tmp/imagesourceserviceprovider-type-cache-retention-results.txt
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

The control demonstrates that the generated types and payloads collect when the provider service cache is cleared. The current run demonstrates that the live image-source service cache alone keeps the concrete types, their collectible assemblies, and their static payloads alive.

This is distinct from the static hosting mapping cache leak: `ImageSourceToImageSourceServiceTypeMapping.s_instances` can retain throwaway image-source service collections, while this repro proves an app-lifetime `ImageSourceServiceProvider` instance retains arbitrary concrete image-source `Type` keys.
