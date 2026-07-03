# MauiServiceCollection Factory Delegate Retention Repro

This repro checks whether MAUI keeps handler and image-source implementation factory delegate targets alive in app-lifetime `MauiServiceCollection` descriptors.

The sample registers static element and image-source types, but uses collectible dynamic factory target instances with 1 MiB payloads:

- `IMauiHandlersCollection.AddHandler<T>(Func<IServiceProvider, IElementHandler>)`
- `IImageSourceServiceCollection.AddService<TImageSource>(Func<IServiceProvider, IImageSourceService<TImageSource>>)`

The dynamic factories are not invoked. The proof is intentionally about registration descriptor retention, not handler creation, image loading, or dynamic type-registration metadata.

## Run

```bash
dotnet run --project src/Core/samples/MauiServiceCollectionFactoryDelegateRetentionRepro/MauiServiceCollectionFactoryDelegateRetentionRepro.csproj -c Release -- --results=/tmp/mauiservicecollection-factory-delegate-retention-results.txt
```

Expected result: the control removes only descriptors whose `ImplementationFactory` contains a dynamic factory delegate while keeping the app/provider live and retains 0 dynamic targets. Current MAUI leaves those descriptors intact and retains every dynamic handler-factory and image-service-factory target, its collectible assembly, and its payload.

Latest local result:

```text
MAUI service collection factory delegate retention repro
Result: PROVEN

Dynamic handler factory targets: 80
Dynamic image-service factory targets: 80
Payload per factory target: 1 MiB

Control: dynamic factory descriptors removed before forced GC while the app remains live
  Handler descriptors before collect: 80
  Handler descriptors after collect: 0
  Image-service descriptors before collect: 80
  Image-service descriptors after collect: 0
  Dynamic handler factory delegates after collect: 0
  Dynamic image-service factory delegates after collect: 0
  Retained handler factory assemblies: 0/80
  Retained handler factory target types: 0/80
  Retained handler factory target instances: 0/80
  Retained handler factory payloads: 0/80
  Retained image-service factory assemblies: 0/80
  Retained image-service factory target types: 0/80
  Retained image-service factory target instances: 0/80
  Retained image-service factory payloads: 0/80
  Retained payload bytes: 0
  Managed heap delta: 118,688 bytes

Current MAUI: dynamic factory descriptors left intact while the app remains live
  Handler descriptors before collect: 80
  Handler descriptors after collect: 80
  Image-service descriptors before collect: 80
  Image-service descriptors after collect: 80
  Dynamic handler factory delegates after collect: 80
  Dynamic image-service factory delegates after collect: 80
  Retained handler factory assemblies: 80/80
  Retained handler factory target types: 80/80
  Retained handler factory target instances: 80/80
  Retained handler factory payloads: 80/80
  Retained image-service factory assemblies: 80/80
  Retained image-service factory target types: 80/80
  Retained image-service factory target instances: 80/80
  Retained image-service factory payloads: 80/80
  Retained payload bytes: 167,772,160
  Managed heap delta: 168,301,656 bytes
```

Optional scale control:

```bash
dotnet run --project src/Core/samples/MauiServiceCollectionFactoryDelegateRetentionRepro/MauiServiceCollectionFactoryDelegateRetentionRepro.csproj -c Release -- --registrations=40 --payload-mib=2
```

## Tracking Check

Official `dotnet/maui` searches for `ImplementationFactory`, `AddHandler` factory, `IImageSourceService` factory, `MauiServiceCollection`, memory, leak, retain, and retention terms found no exact issue for this live service-collection factory-delegate retention path. Fork branch filters for factory delegate, implementation factory, handler factory retention, image-source factory retention, and service factory retention found adjacent metadata/cache repro branches, but no exact live factory-delegate repro.
