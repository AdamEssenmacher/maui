# MapGeopathCollectionRetentionRepro

This sample proves that the public `Polyline.Geopath` and `Polygon.Geopath` collections retain their owning map element when app code keeps those collections after removing or replacing route overlays.

The constructors for `Polyline` and `Polygon` create an `ObservableCollection<Location>` and subscribe an anonymous `CollectionChanged` handler that calls `OnPropertyChanged(nameof(Geopath))`. Because `Geopath` is public, route-oriented apps can keep those collections in a route cache and later continue appending GPS points. The retained collection then keeps the anonymous handler alive, which keeps the old `Polyline` or `Polygon` and its `BindingContext` graph alive.

The autorun compares:

- control: retain the same route collections after clearing the MAUI `Geopath.CollectionChanged` handlers with reflection;
- current: retain the same route collections with the MAUI handlers intact.

The default run creates `80` polylines and `80` polygons, with `256` realistic route points and a `1 MiB` payload per overlay. This represents route/live-location apps that keep route point collections separately from the visible map overlay objects.

## Build

```sh
dotnet build src/Controls/samples/MapGeopathCollectionRetentionRepro/MapGeopathCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

## Run

```sh
open -W artifacts/bin/MapGeopathCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MapGeopathCollectionRetentionRepro.app
cat /tmp/map-geopath-collection-retention-results.txt
```
