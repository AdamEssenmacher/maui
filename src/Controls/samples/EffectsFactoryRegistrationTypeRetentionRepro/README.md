# EffectsFactory Registration Type Retention Repro

This repro proves that the current `Microsoft.Maui.Controls.Hosting.EffectsFactory` registration cache can retain collectible plugin/module assemblies through app-lifetime effect registration metadata.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a `RoutingEffect` subclass and a `PlatformEffect` subclass with a 1 MiB static payload to model a small plugin effect package. It then feeds those pairs through the same `EffectsRegistration` / `EffectsFactory` path used by `ConfigureEffects(...)`.

`EffectsFactory` stores each `RoutingEffect` type as a key in its private `_registeredEffects` dictionary. The dictionary value is a factory delegate that captures the matching `PlatformEffect` type. In the control scenario, the repro clears that dictionary before forcing GC. In current MAUI, the dictionary is left intact.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Controls/samples/EffectsFactoryRegistrationTypeRetentionRepro/EffectsFactoryRegistrationTypeRetentionRepro.csproj -c Release -- --results=/tmp/effectsfactory-registration-type-retention-results.txt
```

Latest local result:

```text
EffectsFactory registration Type retention repro
Result: PROVEN

Trigger:
  ConfigureEffects creates app-lifetime EffectsRegistration entries and an EffectsFactory singleton.
  EffectsFactory builds a _registeredEffects dictionary keyed by RoutingEffect Type.
  Each dictionary value is a platform-effect factory delegate that captures the PlatformEffect Type.
  There is no public unregister or eviction path for plugin/module unload while the factory lives.

Dynamic effect pairs: 80
Payload per platform effect type: 1 MiB

Control: EffectsFactory._registeredEffects cleared before forced GC
  Entries before collect: 80
  Entries after collect: 0
  Retained assemblies: 0/80
  Retained RoutingEffect types: 0/80
  Retained PlatformEffect types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0
  Managed heap delta: 51,032 bytes

Current MAUI: EffectsFactory._registeredEffects left intact
  Entries before collect: 80
  Entries after collect: 80
  Retained assemblies: 80/80
  Retained RoutingEffect types: 80/80
  Retained PlatformEffect types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,964,392 bytes
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/EffectsFactoryRegistrationTypeRetentionRepro/EffectsFactoryRegistrationTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `EffectsFactory memory leak`, `ConfigureEffects memory leak`, `IEffectsBuilder leak OR retention`, `PlatformEffect AssemblyLoadContext memory`, and `RoutingEffect memory leak` found no exact tracking issue. Broad effect searches returned unrelated existing rows such as Android JNI/native-view retention and Windows memory reports. Fork branch filters for `effectsfactory`, `configureeffects`, `ieffectsbuilder`, `routing-effect`, `routingeffect`, `platformeffect`, and effect-type retention found only the adjacent static `Registrar.Effects` repro, not this DI-based `EffectsFactory` registration class.
