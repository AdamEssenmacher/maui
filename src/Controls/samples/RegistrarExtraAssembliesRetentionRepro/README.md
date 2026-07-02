# Registrar ExtraAssemblies Retention Repro

This repro proves that `Microsoft.Maui.Controls.Internals.Registrar.ExtraAssemblies` can retain collectible plugin/module assemblies through process-static compatibility registration state.

The repro creates 80 collectible dynamic assemblies. Each assembly defines one type with a 1 MiB static payload to model a small renderer/plugin/module package. It then assigns those assemblies to the public static `Registrar.ExtraAssemblies` property, which is also assigned by compatibility `Forms.Init(rendererAssemblies)` paths on WPF, GTK, and Windows.

`Registrar.ExtraAssemblies` is used by `Registrar.RegisterAll(...)` to union additional assemblies into registration scans. In the control scenario, the repro clears the static property before forcing GC. In current MAUI, the property is left intact.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: Registrar.ExtraAssemblies cleared before forced GC
  ExtraAssemblies before collect: 80
  ExtraAssemblies after collect: 0
  Retained assemblies: 0/80
  Retained types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0
  Managed heap delta: 40,000 bytes

Current MAUI: Registrar.ExtraAssemblies left intact
  ExtraAssemblies before collect: 80
  ExtraAssemblies after collect: 80
  Retained assemblies: 80/80
  Retained types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,935,800 bytes
```

## Commands

```bash
dotnet run --project src/Controls/samples/RegistrarExtraAssembliesRetentionRepro/RegistrarExtraAssembliesRetentionRepro.csproj -c Release -- --results=/tmp/registrar-extraassemblies-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/RegistrarExtraAssembliesRetentionRepro/RegistrarExtraAssembliesRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `ExtraAssemblies memory leak`, `rendererAssemblies memory leak`, `Forms Init rendererAssemblies retain assembly`, and `ExtraAssemblies AssemblyLoadContext unload` found no exact tracking issue. Fork branch filters for `extraassembl`, `extra-assembl`, `rendererassembl`, `renderer-assembl`, `forms-init assembly`, `default-renderer`, `registrar-extra`, and compatibility assembly-retention terms found no existing repro branch for this class.
