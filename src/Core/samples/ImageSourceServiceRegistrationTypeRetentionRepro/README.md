# ImageSource Service Registration Type Retention Repro

This repro proves that the current `ConfigureImageSources(...)` / `IImageSourceServiceCollection.AddService<TImageSource,TService>()` registration path can retain collectible plugin/module image-source and image-source-service types for the app lifetime.

The repro creates 80 collectible dynamic assemblies during the real `ConfigureImageSources(...)` registration callback. Each assembly defines one image-source type implementing `IImageSource`, one service type implementing `IImageSourceService<TImageSource>`, a 1 MiB static payload on the image-source type, and a 1 MiB static payload on the service type. It closes the public generic `AddService<TImageSource,TService>()` extension method over those dynamic types and keeps the app/service provider alive.

`AddService<TImageSource,TService>()` stores image-source and service types in `ImageSourceToImageSourceServiceTypeMapping` and adds singleton service descriptors to the app-lifetime `MauiServiceCollection`. In the control scenario, the repro clears only the image-source service collection descriptors and mapping dictionaries before forcing GC. In current MAUI, that registration state is left intact.

This is distinct from the C024 hosting service-collection repro: C024 proves a process-static dictionary retains otherwise-dead throwaway image-source service collections. This repro keeps the app-lifetime image-source service collection alive in both scenarios and isolates the dynamic registration metadata inside the live collection.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/ImageSourceServiceRegistrationTypeRetentionRepro/ImageSourceServiceRegistrationTypeRetentionRepro.csproj -c Release -- --results=/tmp/imagesourceservice-registration-type-retention-results.txt
```

Latest local result:

```text
MAUI image-source service registration type-retention repro
Result: PROVEN

Trigger:
  ConfigureImageSources(...) feeds public IImageSourceServiceCollection.AddService<TImageSource,TService>() registrations into the app-lifetime image-source service collection.
  AddService stores each image-source Type and image-source-service Type in ImageSourceToImageSourceServiceTypeMapping and MauiServiceCollection service descriptors.
  There is no public unregister or scoped eviction path for dynamically loaded image-source service registrations while the app-lifetime provider lives.
  Plugin/module image-source service registrations can therefore stay rooted after the plugin should unload.

Dynamic image-source service registrations: 80
Payload per dynamic image-source type: 1 MiB
Payload per dynamic image-source-service type: 1 MiB

Control: IImageSourceServiceCollection descriptors and mappings cleared before forced GC
  Service descriptors before collect: 80
  Service descriptors after collect: 0
  Concrete mappings before collect: 80
  Concrete mappings after collect: 0
  Interface mappings before collect: 0
  Interface mappings after collect: 0
  Retained assemblies: 0
  Retained image-source types: 0
  Retained image-source-service types: 0
  Retained image-source payloads: 0
  Retained service payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 92,200 bytes

Current MAUI: IImageSourceServiceCollection registration state left intact
  Service descriptors before collect: 80
  Service descriptors after collect: 80
  Concrete mappings before collect: 80
  Concrete mappings after collect: 80
  Interface mappings before collect: 0
  Interface mappings after collect: 0
  Retained assemblies: 80
  Retained image-source types: 80
  Retained image-source-service types: 80
  Retained image-source payloads: 80
  Retained service payloads: 80
  Retained payload bytes: 167,772,160
  Managed heap delta: 167,976,784 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/ImageSourceServiceRegistrationTypeRetentionRepro/ImageSourceServiceRegistrationTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `ConfigureImageSources memory leak`, `IImageSourceServiceCollection retention`, `ImageSourceServiceBuilder`, and `ImageSourceToImageSourceServiceTypeMapping memory leak` found no exact tracking issue for app-lifetime image-source service registration metadata retaining collectible image-source/service types. Fork branch filters for `imagesource registration`, `image-source service`, `imagesource-service`, `imagesourceservice`, `configure image`, `image registration`, and `hosting service collection` found only adjacent `origin/repro/hosting-service-collection-cache-leak-20260626` plus provider-cache branches, not this live registration-state class.
