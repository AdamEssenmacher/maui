# DependencyService Registration Type Retention Repro

This repro proves that `Microsoft.Maui.Controls.DependencyService` can retain collectible plugin/module assemblies through static service registration metadata.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a service interface and an implementation type with a 1 MiB static payload to model a small plugin, tenant module, or dynamic compatibility service package. It then invokes the public `DependencyService.Register<T,TImpl>()` path via reflection using those dynamic types.

`DependencyService.Register<T,TImpl>()` stores the service type in the process-static `DependencyTypes` list and stores the implementation type in the process-static `DependencyImplementations` dictionary. In the control scenario, the repro clears both tables before forcing GC. In current MAUI, the tables are left intact.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: explicit DependencyService table clear before forced GC
  DependencyTypes before collect: 80
  DependencyImplementations before collect: 80
  DependencyTypes after collect: 0
  DependencyImplementations after collect: 0
  Retained assemblies: 0/80
  Retained service types: 0/80
  Retained implementor types: 0/80
  Retained payloads: 0/80
  Retained payload bytes: 0

Current MAUI: DependencyService tables left intact
  DependencyTypes before collect: 80
  DependencyImplementations before collect: 80
  DependencyTypes after collect: 80
  DependencyImplementations after collect: 80
  Retained assemblies: 80/80
  Retained service types: 80/80
  Retained implementor types: 80/80
  Retained payloads: 80/80
  Retained payload bytes: 83,886,080
  Managed heap delta: 84,041,360 bytes
```

## Commands

```bash
dotnet run --project src/Controls/samples/DependencyServiceRegistrationTypeRetentionRepro/DependencyServiceRegistrationTypeRetentionRepro.csproj -c Release -- --results=/tmp/dependencyservice-registration-type-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/DependencyServiceRegistrationTypeRetentionRepro/DependencyServiceRegistrationTypeRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `DependencyService memory leak retained type`, `DependencyService Register memory leak`, `DependencyAttribute memory leak`, and `DependencyService AssemblyLoadContext unload retain` found no exact tracking issue. Fork branch filters for `dependencyservice`, `dependency-service`, `dependencyattribute`, `service-locator`, `registration type`, and `static service` found no existing repro branch for this class.
