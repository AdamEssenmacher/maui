# Registrar Registered Type Retention Repro

This repro proves that `Microsoft.Maui.Controls.Internals.Registrar.Registered` can retain collectible plugin/module assemblies through process-static compatibility renderer registration metadata.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a `View` subclass and an `IRegisterable` renderer type with a 1 MiB static payload to model a small compatibility renderer package. It then registers those dynamic view/renderer type pairs with `Registrar.Registered.Register(...)`, the same static table used by compatibility renderer registration paths.

`Registrar.Registered` stores target view `Type` keys and renderer `Type` values in a process-static dictionary. In the control scenario, the repro clears that dictionary before forcing GC. In current MAUI, the dictionary is left intact.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: Registrar.Registered handler table cleared before forced GC
  Registered view entries before collect: 80
  Registered handler entries before collect: 80
  Registered view entries after collect: 0
  Registered handler entries after collect: 0
  Retained assemblies: 0/80
  Retained view types: 0/80
  Retained renderer types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0
  Managed heap delta: 40,352 bytes

Current MAUI: Registrar.Registered handler table left intact
  Registered view entries before collect: 80
  Registered handler entries before collect: 80
  Registered view entries after collect: 80
  Registered handler entries after collect: 80
  Retained assemblies: 80/80
  Retained view types: 80/80
  Retained renderer types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,964,288 bytes
```

## Commands

```bash
dotnet run --project src/Controls/samples/RegistrarRegisteredTypeRetentionRepro/RegistrarRegisteredTypeRetentionRepro.csproj -c Release -- --results=/tmp/registrar-registered-type-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/RegistrarRegisteredTypeRetentionRepro/RegistrarRegisteredTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `Registrar Registered memory leak`, `AddCompatibilityRenderer memory leak`, `ExportRenderer memory leak AssemblyLoadContext`, and `compatibility renderer registration retain type` found no exact tracking issue. Fork branch filters for `registrar registered`, `compatibility renderer retention`, `export renderer`, `handler registrar`, `renderer registration`, `registered type`, and compatibility type-retention terms found no existing repro branch for this class.
