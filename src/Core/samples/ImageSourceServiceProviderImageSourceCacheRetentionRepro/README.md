# ImageSourceServiceProvider ImageSource Type Cache Retention Repro

This repro proves that the current `Microsoft.Maui.Hosting.ImageSourceServiceProvider` image-source type cache can retain collectible plugin/module image-source types through the obsolete public `IImageSourceServiceProvider.GetImageSourceType(Type)` API.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a plugin image-source interface derived from `IImageSource`, a concrete image-source type implementing that interface, and a 1 MiB static payload on the concrete type. It then resolves those concrete types through the real app-lifetime `ImageSourceServiceProvider.GetImageSourceType(...)` path.

`ImageSourceServiceProvider` stores each concrete runtime type as a key in its private `_imageSourceCache` dictionary. In the control scenario, the repro clears that dictionary before forcing GC. In current MAUI, the dictionary is left intact. The sibling C433 repro covers `_serviceCache`; this repro isolates `_imageSourceCache` and keeps `_serviceCache` empty.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/ImageSourceServiceProviderImageSourceCacheRetentionRepro/ImageSourceServiceProviderImageSourceCacheRetentionRepro.csproj -c Release -- --results=/tmp/imagesourceserviceprovider-imagesource-cache-retention-results.txt
```

Latest local result:

```text
ImageSourceServiceProvider image-source Type cache retention repro
Result: PROVEN

Trigger:
  The obsolete public IImageSourceServiceProvider.GetImageSourceType(Type) API maps concrete image-source types to image-source interfaces.
  ImageSourceServiceProvider caches each concrete runtime Type key in its private _imageSourceCache dictionary.
  There is no public cache eviction path while the app-lifetime provider lives.
  Plugin/module image-source types can therefore stay rooted after the plugin should unload.

Dynamic image-source types: 80
Payload per dynamic image-source type: 1 MiB

Control: ImageSourceServiceProvider._imageSourceCache cleared before forced GC
  Service cache entries before collect: 0
  Service cache entries after collect: 0
  Image-source cache entries before collect: 80
  Image-source cache entries after collect: 0
  Retained assemblies: 0
  Retained image-source types: 0
  Retained image-source interfaces: 0
  Retained payloads: 0
  Retained payload bytes: 0
  Managed heap delta: -568 bytes

Current MAUI: ImageSourceServiceProvider._imageSourceCache left intact
  Service cache entries before collect: 0
  Service cache entries after collect: 0
  Image-source cache entries before collect: 80
  Image-source cache entries after collect: 80
  Retained assemblies: 80
  Retained image-source types: 80
  Retained image-source interfaces: 80
  Retained payloads: 80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,915,928 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/ImageSourceServiceProviderImageSourceCacheRetentionRepro/ImageSourceServiceProviderImageSourceCacheRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `GetImageSourceType memory leak`, `_imageSourceCache`, `image source cache`, and `ImageSourceServiceProvider GetImageSourceType` found no exact tracking issue for this obsolete image-source type-cache retention class. Fork branch filters for `imagesourcetype`, `image-source-type`, `imagesource-cache`, `image-source-cache`, `getimagesourcetype`, and `imagesourceserviceprovider` found only the sibling `_serviceCache` branch `origin/repro/imagesourceserviceprovider-type-cache-retention-20260701`.
