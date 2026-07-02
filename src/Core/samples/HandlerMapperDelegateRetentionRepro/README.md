# HandlerMapper Delegate Retention Repro

This repro tests the public static handler mapper customization surface. MAUI exposes static `PropertyMapper` and `CommandMapper` instances such as `ViewHandler.ViewMapper` and `ViewHandler.ViewCommandMapper`. Apps can add or wrap mapper delegates with `Add`, `AppendToMapping`, `PrependToMapping`, or `ModifyMapping`, but there is no public remove or scoped registration API.

The repro creates 80 collectible dynamic property mapper delegate types and 80 collectible dynamic command mapper delegate types. Each type has a 1 MiB static payload to model a plugin/module mapping package. The repro registers those delegates through the real static `ViewHandler` mappers.

The control scenario removes only the repro's internal mapper entries before forcing GC. Current MAUI leaves the entries in the static mappers.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/HandlerMapperDelegateRetentionRepro/HandlerMapperDelegateRetentionRepro.csproj -c Release -- --results=/tmp/handlermapper-delegate-retention-results.txt
```

Latest local result:

```text
HandlerMapper delegate retention repro
Result: PROVEN

Trigger:
  MAUI exposes process-static handler PropertyMapper and CommandMapper instances.
  Public Add/AppendToMapping/PrependToMapping/ModifyMapping APIs store delegates in those mapper dictionaries.
  The mapper dictionaries have no public remove or scoped registration API.
  Plugin/module mapper delegates can therefore stay rooted after the plugin should unload.

Dynamic mappings per mapper: 80
Total dynamic mapper delegates: 160
Payload per dynamic mapper type: 1 MiB

Control: repro mapper entries removed before forced GC
  Property mapper entries before collect: 80
  Property mapper entries after collect: 0
  Command mapper entries before collect: 80
  Command mapper entries after collect: 0
  Retained assemblies: 0
  Retained mapper types: 0
  Retained payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 57,168 bytes

Current MAUI: repro mapper entries left in static mappers
  Property mapper entries before collect: 80
  Property mapper entries after collect: 80
  Command mapper entries before collect: 80
  Command mapper entries after collect: 80
  Retained assemblies: 160
  Retained mapper types: 160
  Retained payloads: 160
  Retained payload bytes: 167,772,160
  Managed heap delta: 167,945,856 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/HandlerMapperDelegateRetentionRepro/HandlerMapperDelegateRetentionRepro.csproj -c Release -- --count-per-mapper=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` searches for `PropertyMapper memory leak`, `CommandMapper memory leak`, `AppendToMapping leak retention`, `handler mapper memory`, `PropertyMapper delegate retain`, and `CommandMapper delegate retain` found no exact tracking issue for static mapper delegate retention. The searches returned unrelated or broader handler, CollectionView, WebView, and Android memory rows. Fork branch filters for `mapper`, `propertymapper`, `commandmapper`, `appendtomapping`, `modifymapping`, `handler-delegate`, and mapper-retention terms found no existing `origin` repro branch.
