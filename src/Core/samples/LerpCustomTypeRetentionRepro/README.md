# Lerp Custom Type Retention Repro

This repro proves that `Microsoft.Maui.Animations.Lerp.Lerps` can retain collectible plugin/module assemblies through public mutable static animation interpolation metadata.

The repro creates 80 collectible dynamic assemblies. Each assembly defines one custom animation value type and registers it in `Lerp.Lerps` with a custom `Lerp` delegate that captures a 1 MiB payload to model plugin animation state. This mirrors a plugin, design surface, or low-code host adding animation interpolation support for generated value types.

`Lerp.Lerps` is a public static `Dictionary<Type, Lerp>`. In the control scenario, the repro removes the dynamically added entries before forcing GC and leaves the built-in MAUI lerps in place. In current MAUI, the custom entries are left intact.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: custom Lerp.Lerps entries cleared before forced GC
  Total lerps before collect: 98
  Custom lerps before collect: 80
  Total lerps after collect: 18
  Custom lerps after collect: 0
  Retained assemblies: 0/80
  Retained custom types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0
  Managed heap delta: 25,936 bytes

Current MAUI: custom Lerp.Lerps entries left intact
  Total lerps before collect: 98
  Custom lerps before collect: 80
  Total lerps after collect: 98
  Custom lerps after collect: 80
  Retained assemblies: 80/80
  Retained custom types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,960,704 bytes
```

## Commands

```bash
dotnet run --project src/Core/samples/LerpCustomTypeRetentionRepro/LerpCustomTypeRetentionRepro.csproj -c Release -- --results=/tmp/lerp-custom-type-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Core/samples/LerpCustomTypeRetentionRepro/LerpCustomTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `Lerp.Lerps memory leak`, `Lerp custom type memory leak`, and `animation lerp memory leak` found no exact tracking issue. Fork branch filters for `lerp`, `animation type retention`, `custom animation retention`, and `animation metadata` found no existing repro branch for this class.
