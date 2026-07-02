# Registrar Effects Type Retention Repro

This repro proves that `Microsoft.Maui.Controls.Internals.Registrar.Effects` can retain collectible plugin/module assemblies through process-static compatibility effect registration metadata.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a `RoutingEffect` subclass with a 1 MiB static payload to model a small compatibility effect package. It then registers those dynamic effect types with the public `Registrar.RegisterEffect(...)` API, the same static table populated by `ExportEffectAttribute` scanning and `AddCompatibilityEffects(...)`.

`Registrar.Effects` stores effect `Type` values in a process-static dictionary. In the control scenario, the repro clears that dictionary before forcing GC. In current MAUI, the dictionary is left intact.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: Registrar.Effects cleared before forced GC
  Effects before collect: 80
  Effects after collect: 0
  Retained assemblies: 0/80
  Retained effect types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0
  Managed heap delta: 40,392 bytes

Current MAUI: Registrar.Effects left intact
  Effects before collect: 80
  Effects after collect: 80
  Retained assemblies: 80/80
  Retained effect types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,930,728 bytes
```

## Commands

```bash
dotnet run --project src/Controls/samples/RegistrarEffectsTypeRetentionRepro/RegistrarEffectsTypeRetentionRepro.csproj -c Release -- --results=/tmp/registrar-effects-type-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/RegistrarEffectsTypeRetentionRepro/RegistrarEffectsTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `Registrar Effects memory leak`, `RegisterEffect memory leak`, `ExportEffect memory leak AssemblyLoadContext`, and `AddCompatibilityEffects memory leak` found no exact tracking issue. Fork branch filters for `effect registrar`, `registereffect`, `exporteffect`, `compatibility effect retention`, `effect type retention`, and `routingeffect` found no existing repro branch for this class.
