# FontRegistrar Embedded Assembly Retention Repro

This repro proves that `Microsoft.Maui.FontRegistrar` can retain collectible plugin/module assemblies through embedded-resource font registration.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a type with a 1 MiB static payload to model a small plugin, tenant module, or dynamic UI package. It then calls the real `FontRegistrar.Register(filename, alias, assembly)` path for one embedded-resource font per assembly.

`FontRegistrar.Register(...)` stores the supplied `Assembly` as part of the `_embeddedFonts` dictionary value, and an alias creates a second dictionary entry for the same assembly. In the control scenario, the repro clears `_embeddedFonts` before forcing GC. In current MAUI, the registrar is left intact, as it would be for an app-level singleton.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: explicit _embeddedFonts.Clear() before forced GC
  Entries before collect: 160
  Entries after collect: 0
  Retained assemblies: 0/80
  Retained types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0

Current MAUI: _embeddedFonts left intact
  Entries before collect: 160
  Entries after collect: 160
  Retained assemblies: 80/80
  Retained types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,959,656 bytes
```

## Commands

```bash
dotnet run --project src/Controls/samples/FontRegistrarEmbeddedAssemblyRetentionRepro/FontRegistrarEmbeddedAssemblyRetentionRepro.csproj -c Release -- --results=/tmp/fontregistrar-embedded-assembly-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/FontRegistrarEmbeddedAssemblyRetentionRepro/FontRegistrarEmbeddedAssemblyRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `FontRegistrar memory leak`, `AddEmbeddedResourceFont memory leak`, `ExportFont memory leak`, `embedded resource font assembly leak`, and `font registrar assembly retain` found no exact tracking issue. Fork branch filters for `font`, `Font`, `registrar`, `dynamic font`, and `embedded font` found no existing repro branch for this class.
