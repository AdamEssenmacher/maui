# XamlLoader Assembly Cache Retention Repro

This repro proves that `Microsoft.Maui.Controls.Xaml.XamlLoader.Load(...)` can retain collectible assemblies through the static `XamlParser.s_allowImplicitXmlns` dictionary.

The repro creates 80 collectible dynamic assemblies. Each assembly defines a type with a 1 MiB static payload to model a small plugin, tenant module, or dynamic UI package. It then invokes the real internal `XamlLoader.Load(object, string, Assembly, bool)` path with each assembly as `rootAssembly`.

`XamlLoader.Load(...)` stores `rootAssembly` as a strong key in `XamlParser.s_allowImplicitXmlns` before XAML hydration. In the control scenario, the repro clears that cache before forcing GC. In current MAUI, the cache is left intact.

## Result

Local run on 2026-07-01:

```text
Control cleared cache: cache=0, assemblies=0/80, payloads=0/80, retainedPayload=0.0 MiB
Current MAUI: cache=80, assemblies=80/80, payloads=80/80, retainedPayload=80.0 MiB
```

## Commands

```bash
dotnet run --project src/Controls/samples/XamlLoaderAssemblyCacheRetentionRepro/XamlLoaderAssemblyCacheRetentionRepro.csproj -c Release -- --results=/tmp/xamlloader-assembly-cache-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/XamlLoaderAssemblyCacheRetentionRepro/XamlLoaderAssemblyCacheRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `s_allowImplicitXmlns memory leak` and `XamlLoader Assembly memory leak` found no exact tracking issue. Fork branch filters for `implicit-xmlns`, `allowimplicit`, `xamlparser`, `xamlloader`, `assemblyload`, and dynamic assembly terms found only the unrelated/adjacent `origin/repro/shell-canceled-push-implicit-route-leak-20260625` branch, not an existing XamlLoader assembly-cache repro.
